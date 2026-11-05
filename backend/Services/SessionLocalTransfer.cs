using System.Formats.Tar;
using System.IO.Compression;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace AgentHub.Api.Services;

/// <summary>Why a transfer cannot go ahead, as a fixed code the MCP tools and the web app show.</summary>
public sealed class SessionTransferException(string code, string message) : Exception(message)
{
    public string Code { get; } = code;
}

/// <summary>The session's transcript, prepared for one directory on a workstation.</summary>
public sealed record LocalTranscriptArchive(string ResumeId, string ProjectFolder, byte[] Archive, int Files);

/// <summary>
/// Moves a Claude Code conversation between a session's state archive and a workstation
/// (docs/session-transfer.md). Pure: archive bytes in, archive bytes out, so every decision is
/// tested against real tar files without a cluster or object storage.
///
/// Claude Code files a conversation under its working directory — the project folder is that path
/// with every character outside [A-Za-z0-9] replaced by a dash, and every transcript line repeats
/// it in <c>cwd</c>. The pod's <c>/workspace/repo</c> and a laptop's project path disagree on both,
/// so a copy resumes nothing; both directions retarget the folder and rewrite <c>cwd</c>.
/// </summary>
public static partial class SessionLocalTransfer
{
    public const string Projects = ".claude/projects";
    /// <summary>Written by the Claude driver next to the transcripts; the id the pod resumes, which
    /// a context clear changes, so it outranks the session record.</summary>
    public const string SessionIdMarker = ".claude/agenthub-session-id";

    private static readonly JsonSerializerOptions Relaxed = new()
    {
        // The default encoder escapes every non-ASCII character and '<', '>', '&', '+'. A transcript
        // line rewritten with it would differ from the one the CLI wrote in every such character,
        // and the resuming pod compares the restored file with the hub's copy byte for byte (#146).
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    [GeneratedRegex("^[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}$")]
    private static partial Regex Uuid();

    /// <summary>The folder name Claude Code derives from a working directory.</summary>
    public static string ProjectFolder(string directory) => Regex.Replace(directory, "[^A-Za-z0-9]", "-");

    /// <summary>
    /// A directory the local CLI can be started in. Absolute, because the folder name is derived
    /// from the exact string: a relative path would file the conversation where no CLI looks.
    /// </summary>
    public static string ValidateLocalDirectory(string? directory)
    {
        var value = directory?.Trim() ?? "";
        if (value.Length is 0 or > 1024 || value.Any(char.IsControl))
            throw new SessionTransferException("directory_invalid", "Give the absolute path of the local project directory.");
        var posix = value.StartsWith('/');
        var windows = value.Length >= 3 && char.IsAsciiLetter(value[0]) && value[1] == ':' && value[2] is '\\' or '/';
        if (!posix && !windows)
            throw new SessionTransferException("directory_invalid",
                "Give the absolute path of the local project directory, e.g. /home/me/project or C:\\Users\\me\\project.");
        // A trailing separator would change the folder name ("-home-me-project-") and the CLI,
        // which records the directory without it, would not find the conversation.
        return value.Length > 3 ? value.TrimEnd('/', '\\') : value;
    }

    /// <summary>
    /// The session's transcript and its sidecar files, retargeted at <paramref name="localDirectory"/>
    /// and packed relative to the home directory, so <c>tar -xzf - -C ~</c> puts them where
    /// <c>claude --resume</c> looks.
    /// </summary>
    public static LocalTranscriptArchive ForLocal(Stream stateArchive, string localDirectory)
    {
        var state = Read(stateArchive);
        var (folder, resumeId) = FindConversation(state);
        var target = ProjectFolder(localDirectory);
        var files = SessionFiles(state, folder, resumeId)
            .Select(file => Retarget(file, target, resumeId, localDirectory, sessionId: null))
            .ToList();
        return new LocalTranscriptArchive(resumeId, target, Write(files), files.Count);
    }

    /// <summary>
    /// The stored state with the conversation replaced by the one uploaded from a workstation. Only
    /// that conversation's files change; the rest of the home directory stays as the pod left it.
    /// The upload is renamed to the id the pod resumes: a transcript stored under the local id
    /// would be ignored, and the session would come back with none of the local turns.
    /// </summary>
    public static byte[] MergeFromLocal(Stream stateArchive, Stream upload)
    {
        var state = Read(stateArchive);
        var (folder, resumeId) = FindConversation(state);
        var remoteDirectory = RecordedDirectory(state, folder, resumeId)
            ?? throw new SessionTransferException("transcript_missing",
                "The stored transcript does not say which directory the session works in.");
        var local = ReadUpload(upload);

        var stored = state.First(file => file.Name == $"{Projects}/{folder}/{resumeId}.jsonl");
        if (FirstMessageId(stored.Data) is { } first
            && !Encoding.UTF8.GetString(local.Transcript.Data).Contains($"\"{first}\"", StringComparison.Ordinal))
            // The CLI only appends, so a continued conversation still holds its first message. One that
            // does not is another conversation, and storing it would replace the session's history.
            throw new SessionTransferException("transcript_unrelated",
                "The local transcript is not a continuation of this session's conversation.");

        var replaced = local.Files
            .Select(file => Retarget(file, folder, resumeId, remoteDirectory, sessionId: resumeId))
            .ToList();
        var kept = state.Where(file => !BelongsTo(file.Name, folder, resumeId));
        return Write([.. kept, .. replaced]);
    }

    /// <summary>The conversation id a hub session resumes, read from its stored state.</summary>
    public static string ResumeId(Stream stateArchive) => FindConversation(Read(stateArchive)).ResumeId;

    // ------------------------------------------------------------------ archive model

    private sealed record ArchiveFile(string Name, TarEntry Entry, byte[] Data);

    private static List<ArchiveFile> Read(Stream gzip)
    {
        var files = new List<ArchiveFile>();
        using var unzipped = new GZipStream(gzip, CompressionMode.Decompress, leaveOpen: true);
        using var reader = new TarReader(unzipped);
        while (reader.GetNextEntry(copyData: true) is { } entry)
        {
            var data = Array.Empty<byte>();
            if (entry.DataStream is { } stream)
            {
                using var buffer = new MemoryStream();
                stream.CopyTo(buffer);
                data = buffer.ToArray();
            }
            files.Add(new ArchiveFile(Normalise(entry.Name), entry, data));
        }
        return files;
    }

    private static byte[] Write(IEnumerable<ArchiveFile> files)
    {
        using var output = new MemoryStream();
        using (var zip = new GZipStream(output, CompressionLevel.Optimal, leaveOpen: true))
        using (var writer = new TarWriter(zip, TarEntryFormat.Pax, leaveOpen: true))
        {
            foreach (var file in files)
            {
                // Entries read from the pod's archive are GNU or ustar; a writer only takes its own
                // format, and the conversion keeps mode, owner, times and link targets.
                var entry = file.Entry is PaxTarEntry pax ? pax : new PaxTarEntry(file.Entry);
                if (entry.EntryType is TarEntryType.RegularFile or TarEntryType.V7RegularFile)
                    entry.DataStream = new MemoryStream(file.Data, writable: false);
                writer.WriteEntry(entry);
            }
        }
        return output.ToArray();
    }

    private static string Normalise(string name)
    {
        var value = name.Replace('\\', '/');
        while (value.StartsWith("./", StringComparison.Ordinal)) value = value[2..];
        return value;
    }

    // ------------------------------------------------------------------ conversation lookup

    private static (string Folder, string ResumeId) FindConversation(List<ArchiveFile> state)
    {
        var transcripts = state
            .Where(file => file.Entry.EntryType is TarEntryType.RegularFile or TarEntryType.V7RegularFile)
            .Select(file => (file, path: Parse(file.Name)))
            .Where(item => item.path is { Kind: PathKind.Transcript })
            .Select(item => (item.file, path: item.path!.Value))
            .ToList();
        if (transcripts.Count == 0)
            throw new SessionTransferException("transcript_missing",
                "The stored state holds no Claude Code conversation. A session has one once it has run.");

        var marker = state.FirstOrDefault(file => file.Name == SessionIdMarker);
        var marked = marker is null ? "" : Encoding.UTF8.GetString(marker.Data).Trim();
        if (Uuid().IsMatch(marked) && transcripts.FirstOrDefault(item => item.path.SessionId == marked) is { file: not null } hit)
            return (hit.path.Folder, marked);
        // No marker from an older runtime: the newest transcript is the one the pod was writing
        // when it stopped. Several are normal — a context clear starts a new one.
        var newest = transcripts.MaxBy(item => item.file.Entry.ModificationTime);
        return (newest.path.Folder, newest.path.SessionId);
    }

    private static string? RecordedDirectory(List<ArchiveFile> state, string folder, string resumeId)
    {
        var transcript = state.First(file => file.Name == $"{Projects}/{folder}/{resumeId}.jsonl");
        foreach (var line in Encoding.UTF8.GetString(transcript.Data).Split('\n'))
        {
            if (TryObject(line) is { } node && node["cwd"] is JsonValue cwd && cwd.TryGetValue<string>(out var value)
                && ProjectFolder(value) == folder)
                return value;
        }
        return null;
    }

    private static string? FirstMessageId(byte[] transcript)
    {
        foreach (var line in Encoding.UTF8.GetString(transcript).Split('\n'))
            if (TryObject(line) is { } node && node["uuid"] is JsonValue uuid && uuid.TryGetValue<string>(out var value)
                && !string.IsNullOrEmpty(value))
                return value;
        return null;
    }

    private enum PathKind { Transcript, Sidecar }
    private readonly record struct ProjectPath(string Folder, string SessionId, PathKind Kind, string Rest);

    /// <summary>
    /// <c>.claude/projects/&lt;folder&gt;/&lt;id&gt;.jsonl</c> or a file below
    /// <c>.claude/projects/&lt;folder&gt;/&lt;id&gt;/</c> (subagent transcripts, tool results).
    /// </summary>
    private static ProjectPath? Parse(string name)
    {
        if (!name.StartsWith(Projects + "/", StringComparison.Ordinal)) return null;
        var parts = name[(Projects.Length + 1)..].TrimEnd('/').Split('/');
        if (parts.Length < 2 || parts.Any(part => part is "" or "." or "..")) return null;
        if (parts.Length == 2 && parts[1].EndsWith(".jsonl", StringComparison.Ordinal))
        {
            var id = parts[1][..^".jsonl".Length];
            return Uuid().IsMatch(id) ? new ProjectPath(parts[0], id, PathKind.Transcript, "") : null;
        }
        // A project folder holds other directories too (memory/ is one Claude Code writes); only a
        // directory named like a conversation id is a conversation's sidecar.
        return parts.Length > 2 && Uuid().IsMatch(parts[1])
            ? new ProjectPath(parts[0], parts[1], PathKind.Sidecar, string.Join('/', parts[2..]))
            : null;
    }

    private static bool BelongsTo(string name, string folder, string sessionId)
        => Parse(name) is { } path && path.Folder == folder && path.SessionId == sessionId;

    private static IEnumerable<ArchiveFile> SessionFiles(List<ArchiveFile> state, string folder, string sessionId)
        => state.Where(file => file.Entry.EntryType is TarEntryType.RegularFile or TarEntryType.V7RegularFile
                               && BelongsTo(file.Name, folder, sessionId));

    // ------------------------------------------------------------------ the upload

    private sealed record Upload(ArchiveFile Transcript, List<ArchiveFile> Files);

    /// <summary>
    /// What a workstation sent: one conversation's transcript and its sidecars, nothing else. The
    /// names come from someone else's tar, so anything outside that shape is refused rather than
    /// merged — it would land in the pod's home directory on the next resume.
    /// </summary>
    private static Upload ReadUpload(Stream upload)
    {
        List<ArchiveFile> files;
        try { files = Read(upload); }
        catch (Exception error) when (error is InvalidDataException or FormatException or EndOfStreamException)
        { throw new SessionTransferException("upload_invalid", "The upload is not a gzip-compressed tar archive."); }

        var regular = new List<ArchiveFile>();
        foreach (var file in files)
        {
            if (file.Entry.EntryType is TarEntryType.Directory) continue;
            if (file.Entry.EntryType is not (TarEntryType.RegularFile or TarEntryType.V7RegularFile) || Parse(file.Name) is null)
                throw new SessionTransferException("upload_invalid",
                    $"Unexpected entry in the upload: {file.Name}. Send only {Projects}/<folder>/<id>.jsonl and its <id>/ folder.");
            regular.Add(file);
        }
        var transcripts = regular.Where(file => Parse(file.Name)!.Value.Kind == PathKind.Transcript).ToList();
        if (transcripts.Count != 1)
            throw new SessionTransferException("upload_invalid",
                transcripts.Count == 0 ? "The upload holds no transcript." : "The upload holds more than one transcript.");
        var main = Parse(transcripts[0].Name)!.Value;
        if (regular.Any(file => Parse(file.Name) is { } path && (path.Folder != main.Folder || path.SessionId != main.SessionId)))
            throw new SessionTransferException("upload_invalid", "The upload mixes files of several conversations.");
        return new Upload(transcripts[0], regular);
    }

    // ------------------------------------------------------------------ rewrite

    private static ArchiveFile Retarget(ArchiveFile file, string folder, string resumeId, string directory, string? sessionId)
    {
        var path = Parse(file.Name)!.Value;
        var name = path.Kind == PathKind.Transcript
            ? $"{Projects}/{folder}/{resumeId}.jsonl"
            : $"{Projects}/{folder}/{resumeId}/{path.Rest}";
        // Only the transcript carries cwd that the resume reads. The sidecars are subagent
        // conversations, not indexed by path; rewriting them would edit history nothing reads.
        var data = path.Kind == PathKind.Transcript ? RewriteTranscript(file.Data, directory, sessionId) : file.Data;
        var entry = new PaxTarEntry(TarEntryType.RegularFile, name)
        {
            // Owner-only: a transcript holds everything the conversation saw, and the default umask
            // on a shared workstation would leave it readable to every account there.
            Mode = UnixFileMode.UserRead | UnixFileMode.UserWrite,
            ModificationTime = file.Entry.ModificationTime
        };
        return new ArchiveFile(name, entry, data);
    }

    /// <summary>
    /// Sets <c>cwd</c> (and, handing back, <c>sessionId</c>) on every transcript line that has the
    /// field and leaves every other byte alone. A line that is not JSON is kept: the last line of a
    /// transcript captured mid-write is often cut off, and dropping it would lose the turn before.
    /// </summary>
    public static byte[] RewriteTranscript(byte[] transcript, string cwd, string? sessionId)
    {
        var text = Encoding.UTF8.GetString(transcript);
        var lines = text.Split('\n');
        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i];
            var carriage = line.EndsWith('\r');
            if (TryObject(carriage ? line[..^1] : line) is not { } node) continue;
            var changed = false;
            if (node.ContainsKey("cwd") && !(node["cwd"] is JsonValue c && c.TryGetValue<string>(out var current) && current == cwd))
            {
                node["cwd"] = cwd;
                changed = true;
            }
            if (sessionId is not null && node.ContainsKey("sessionId")
                && !(node["sessionId"] is JsonValue s && s.TryGetValue<string>(out var id) && id == sessionId))
            {
                node["sessionId"] = sessionId;
                changed = true;
            }
            if (changed) lines[i] = node.ToJsonString(Relaxed) + (carriage ? "\r" : "");
        }
        return Encoding.UTF8.GetBytes(string.Join('\n', lines));
    }

    private static JsonObject? TryObject(string line)
    {
        if (string.IsNullOrWhiteSpace(line) || line.TrimStart()[0] != '{') return null;
        try { return JsonNode.Parse(line) as JsonObject; }
        catch (JsonException) { return null; }
    }
}
