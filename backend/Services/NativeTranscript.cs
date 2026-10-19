using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace AgentHub.Api.Services;

public sealed record TranscriptAttachment(string Id, string Name);

/// <summary>
/// One turn of a conversation, read from the provider's own transcript file.
/// </summary>
/// <param name="Role"><c>user</c>, <c>assistant</c>, <c>tool</c> (a call, with <paramref name="Tool"/>
/// naming it) or <c>result</c> (what the call returned).</param>
/// <param name="Text">The text of the turn; tool arguments and results are cut to
/// <see cref="NativeTranscript.MaxToolChars"/>.</param>
/// <param name="At">The provider's timestamp for the line, when it recorded one.</param>
/// <param name="Tool">The tool a <c>tool</c> entry called.</param>
public sealed record TranscriptEntry(string Role, string Text, DateTime? At, string? Tool = null)
{
    public IReadOnlyList<TranscriptAttachment>? Attachments { get; init; }
    [System.Text.Json.Serialization.JsonIgnore]
    public string? PromptId { get; init; }
}

/// <summary>
/// Reads the JSONL files the agent CLIs write for themselves — Claude Code's project transcript,
/// Codex's rollout, and the stream-json a chat session's pipe carries — into role-tagged entries.
///
/// The formats are different but both are "one JSON object per line, a type tag, a message with
/// a role and content parts", so one reader with a per-line format switch is simpler than two
/// readers plus a dispatcher. A line that does not parse, or is a record type the reader does
/// not know (file snapshots, token counts, reasoning), is skipped: a transcript that trails off
/// mid-write must still render what it has.
/// </summary>
public static partial class NativeTranscript
{
    public const int MaxToolChars = 2_000;

    // Codex wraps the instructions and environment it injects as user messages in a single XML
    // element; a person never typed those, and rendering them as "User" would make every
    // transcript open with the contents of AGENTS.md.
    [GeneratedRegex(@"^<([a-z_]+)>[\s\S]*</\1>\s*$")]
    private static partial Regex WrappedInjection();

    // Claude Code records a collapsed paste in the prompt as <pasted_content id="1a2b">, newline,
    // the pasted text, newline, </pasted_content id="1a2b">, set off by up to two newlines on each
    // side. Its terminal shows the paste without them; left in, every pasted log or stack trace
    // reads as markup in the Workspace. The id is four hex digits and the closing tag repeats it,
    // so only a wrapper the CLI wrote matches, never angle brackets the person typed.
    [GeneratedRegex("""\n{0,2}<pasted_content id="([0-9a-f]{4})">\n([\s\S]*?)\n</pasted_content id="\1">\n{0,2}""")]
    private static partial Regex PastedContent();

    public static IReadOnlyList<TranscriptEntry> Parse(string? jsonl)
    {
        return Append([], jsonl);
    }

    public static IReadOnlyList<TranscriptEntry> Append(IReadOnlyList<TranscriptEntry> previous, string? jsonl)
    {
        var entries = previous.ToList();
        if (string.IsNullOrEmpty(jsonl)) return entries;
        foreach (var raw in jsonl.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            if (line.Length == 0 || line[0] != '{') continue;
            try
            {
                using var document = JsonDocument.Parse(line);
                var root = document.RootElement;
                if (root.ValueKind != JsonValueKind.Object) continue;
                if (root.TryGetProperty("type", out var kind) && kind.ValueKind == JsonValueKind.String && kind.GetString() == "agenthub_conversation")
                {
                    var id = root.TryGetProperty("id", out var identity) ? identity.GetString() : null;
                    entries.Add(new TranscriptEntry("boundary", "Conversation cleared", null, id));
                }
                else if (root.TryGetProperty("payload", out var payload) && payload.ValueKind == JsonValueKind.Object)
                    ParseCodexLine(root, payload, entries);
                else
                    ParseClaudeLine(root, entries);
            }
            catch (JsonException)
            {
                // A truncated last line while the agent is still writing, or a cap cut that
                // landed inside a record: nothing to recover from it.
            }
        }
        return entries;
    }

    /// <summary>
    /// The transcript as plain text, for callers that read rather than render: the remote API,
    /// the MCP tools and the chat relays. Append-only in the same way the entries are, so a
    /// character offset into it stays valid as the session goes on.
    /// </summary>
    public static string Render(IReadOnlyList<TranscriptEntry> entries)
    {
        var sb = new StringBuilder();
        foreach (var entry in entries)
        {
            if (sb.Length > 0) sb.Append('\n');
            sb.Append("## ").Append(entry.Role switch
            {
                "user" => "User",
                "assistant" => "Assistant",
                "tool" => "Tool: " + (entry.Tool ?? "?"),
                "result" => "Result",
                _ => entry.Role
            }).Append('\n').Append(entry.Text.TrimEnd()).Append('\n');
        }
        return sb.ToString();
    }

    /// <summary>
    /// The last <paramref name="maxChars"/> characters, cut forward to a line boundary so the
    /// first line kept is a whole record. Same rule the session agent applies before uploading.
    /// </summary>
    public static string TrimToLineCap(string jsonl, int maxChars)
    {
        if (jsonl.Length <= maxChars) return jsonl;
        var cut = jsonl[^maxChars..];
        var newline = cut.IndexOf('\n');
        return newline < 0 ? cut : cut[(newline + 1)..];
    }

    // ------------------------------------------------------------------ Claude Code

    private static void ParseClaudeLine(JsonElement root, List<TranscriptEntry> entries)
    {
        var type = root.TryGetProperty("type", out var t) && t.ValueKind == JsonValueKind.String ? t.GetString() : null;
        // A mid-turn human prompt is delivered beside the next tool result as an attachment.
        // Queue operations describe scheduling and can be cancelled; they do not prove delivery.
        if (type == "attachment" && !IsTrue(root, "isSidechain") &&
            root.TryGetProperty("attachment", out var attachment) && attachment.ValueKind == JsonValueKind.Object &&
            StringOr(attachment, "type") == "queued_command" && StringOr(attachment, "commandMode") == "prompt" &&
            IsTrue(attachment, "humanTurn") && attachment.TryGetProperty("origin", out var origin) &&
            StringOr(origin, "kind") == "human")
        {
            AddText(entries, "user", UnwrapPastes(ContentText(attachment, "prompt")), Timestamp(root, "timestamp"));
            return;
        }
        if (type is not ("user" or "assistant")) return;
        // Meta lines are the CLI talking to itself (command caveats, injected tool guidance);
        // sidechains are sub-agents whose turns the main conversation already summarises.
        if (IsTrue(root, "isSidechain")) return;
        if (IsTrue(root, "isMeta"))
        {
            // The CLI keeps source paths in a companion with the same prompt identity. It can
            // arrive in the next HTTP chunk; matching against previous entries avoids a lost image.
            if (IsTrue(root, "turnCompanion") && StringOr(root, "promptId") is { } promptId &&
                root.TryGetProperty("message", out var companion))
            {
                var attachments = PathAttachments(ContentText(companion, "content"));
                var index = entries.FindLastIndex(entry => entry.Role == "user" && entry.PromptId == promptId);
                if (index >= 0 && attachments.Count > 0)
                    entries[index] = WithAttachments(entries[index], attachments);
            }
            return;
        }
        if (!root.TryGetProperty("message", out var message) || message.ValueKind != JsonValueKind.Object) return;
        var at = Timestamp(root, "timestamp");

        if (!message.TryGetProperty("content", out var content)) return;
        if (content.ValueKind == JsonValueKind.String)
        {
            var value = content.GetString();
            // Clear commands are CLI bookkeeping; the Workspace renders its own readable boundary.
            if (type == "user" && value is not null &&
                (value.StartsWith("<command-name>/clear</command-name>", StringComparison.Ordinal) ||
                 value.StartsWith("<command-name>/new</command-name>", StringComparison.Ordinal))) return;
            AddText(entries, type, type == "user" ? UnwrapPastes(value) : value, at);
            return;
        }
        if (content.ValueKind != JsonValueKind.Array) return;

        var first = entries.Count;
        var hasImage = false;
        var files = new List<TranscriptAttachment>();
        var text = new StringBuilder();
        foreach (var part in content.EnumerateArray())
        {
            var partType = part.TryGetProperty("type", out var pt) ? pt.GetString() : null;
            switch (partType)
            {
                case "file":
                    if (type == "user" && part.TryGetProperty("source", out var source) &&
                        StringOr(source, "path") is { } sourcePath)
                        files.AddRange(PathAttachments(sourcePath));
                    hasImage = true;
                    break;
                case "image":
                    hasImage = true;
                    break;
                case "text":
                    if (part.TryGetProperty("text", out var textValue) && textValue.ValueKind == JsonValueKind.String)
                    {
                        if (text.Length > 0) text.Append('\n');
                        var value = textValue.GetString();
                        text.Append(type == "user" ? UnwrapPastes(value) : value);
                    }
                    break;
                case "tool_use":
                    Flush(entries, type, text, at);
                    entries.Add(new TranscriptEntry("tool",
                        Clip(part.TryGetProperty("input", out var input) ? input.GetRawText() : ""), at,
                        part.TryGetProperty("name", out var name) ? name.GetString() : null));
                    break;
                case "tool_result":
                    Flush(entries, type, text, at);
                    entries.Add(new TranscriptEntry("result", Clip(ContentText(part, "content")), at));
                    break;
            }
        }
        Flush(entries, type, text, at);
        if (type == "user")
        {
            if (hasImage && entries.Count == first) entries.Add(new TranscriptEntry("user", "", at));
            for (var index = first; index < entries.Count; index++)
                if (entries[index].Role == "user")
                    entries[index] = WithAttachments(entries[index] with { PromptId = StringOr(root, "promptId") },
                        PathAttachments(entries[index].Text).Concat(files).ToArray());
        }
    }

    /// <summary>
    /// The prompt as the CLI's terminal shows it once submitted: a paste of several lines on lines
    /// of its own, a one-line paste inline where it was inserted.
    /// </summary>
    public static string? UnwrapPastes(string? text)
    {
        if (text is null || !text.Contains("<pasted_content id=\"", StringComparison.Ordinal)) return text;
        var sb = new StringBuilder();
        var last = 0;
        var previousMultiline = false;
        foreach (Match match in PastedContent().Matches(text))
        {
            var body = match.Groups[2].Value;
            var multiline = body.Contains('\n');
            var typed = text[last..match.Index];
            Join(sb, typed, previousMultiline, typed: true);
            Join(sb, body, multiline || (typed.Length == 0 && previousMultiline), typed: false);
            last = match.Index + match.Length;
            previousMultiline = multiline;
        }
        Join(sb, text[last..], previousMultiline, typed: true);
        return sb.ToString();
    }

    // The regex took the newlines the CLI put around the paste; what joins the pieces instead is
    // a line break next to a paste of several lines, else a space where two words would touch.
    private static void Join(StringBuilder sb, string piece, bool lineBreak, bool typed)
    {
        if (piece.Length == 0) return;
        if (sb.Length > 0 && lineBreak)
        {
            while (sb.Length > 0 && char.IsWhiteSpace(sb[^1])) sb.Length--;
            if (typed) piece = piece.TrimStart();
            if (piece.Length == 0) return;
            sb.Append('\n');
        }
        else if (sb.Length > 0 && !char.IsWhiteSpace(sb[^1]) && !char.IsWhiteSpace(piece[0])) sb.Append(' ');
        sb.Append(piece);
    }

    // ------------------------------------------------------------------ Codex

    private static void ParseCodexLine(JsonElement root, JsonElement payload, List<TranscriptEntry> entries)
    {
        var lineType = root.TryGetProperty("type", out var lt) ? lt.GetString() : null;
        // event_msg lines repeat what response_item lines already carry; keeping both would
        // show every message twice.
        if (lineType != "response_item") return;
        var at = Timestamp(root, "timestamp");
        var itemType = payload.TryGetProperty("type", out var it) ? it.GetString() : null;
        switch (itemType)
        {
            case "message":
            {
                var role = payload.TryGetProperty("role", out var r) ? r.GetString() : null;
                if (role is not ("user" or "assistant")) return;
                var text = ContentText(payload, "content");
                if (role == "user" && WrappedInjection().IsMatch(text.Trim())) return;
                AddText(entries, role, text, at);
                return;
            }
            case "function_call":
            case "custom_tool_call":
                entries.Add(new TranscriptEntry("tool",
                    Clip(StringOr(payload, "arguments") ?? StringOr(payload, "input") ?? ""), at,
                    StringOr(payload, "name")));
                return;
            case "local_shell_call":
                entries.Add(new TranscriptEntry("tool", Clip(ShellCommand(payload)), at, "shell"));
                return;
            case "function_call_output":
            case "custom_tool_call_output":
                entries.Add(new TranscriptEntry("result", Clip(ContentText(payload, "output")), at));
                return;
        }
    }

    private static string ShellCommand(JsonElement payload)
    {
        if (!payload.TryGetProperty("action", out var action) || action.ValueKind != JsonValueKind.Object) return "";
        if (action.TryGetProperty("command", out var command) && command.ValueKind == JsonValueKind.Array)
            return string.Join(' ', command.EnumerateArray().Select(c => c.GetString() ?? ""));
        return action.GetRawText();
    }

    // ------------------------------------------------------------------ shared

    private static void AddText(List<TranscriptEntry> entries, string role, string? text, DateTime? at)
    {
        if (!string.IsNullOrWhiteSpace(text))
            entries.Add(role == "user" ? WithAttachments(new TranscriptEntry(role, text, at), PathAttachments(text))
                : new TranscriptEntry(role, text, at));
    }

    private static void Flush(List<TranscriptEntry> entries, string role, StringBuilder text, DateTime? at)
    {
        if (text.Length == 0) return;
        AddText(entries, role, text.ToString(), at);
        text.Clear();
    }

    /// <summary>A content part that is either a string or an array of typed text parts.</summary>
    private static string ContentText(JsonElement element, string property)
    {
        if (!element.TryGetProperty(property, out var value)) return "";
        switch (value.ValueKind)
        {
            case JsonValueKind.String:
                return value.GetString() ?? "";
            case JsonValueKind.Array:
                return string.Join('\n', value.EnumerateArray()
                    .Select(part => part.ValueKind == JsonValueKind.String
                        ? part.GetString()
                        : part.TryGetProperty("text", out var t) && t.ValueKind == JsonValueKind.String ? t.GetString() : null)
                    .Where(s => !string.IsNullOrEmpty(s)));
            case JsonValueKind.Object:
                // Codex wraps a tool's output as {content, success}.
                return ContentText(value, "content");
            default:
                return "";
        }
    }

    private static string? StringOr(JsonElement element, string property)
        => element.ValueKind == JsonValueKind.Object && element.TryGetProperty(property, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static bool IsTrue(JsonElement element, string property)
        => element.TryGetProperty(property, out var v) && v.ValueKind == JsonValueKind.True;

    private static DateTime? Timestamp(JsonElement element, string property)
        => element.TryGetProperty(property, out var v) && v.ValueKind == JsonValueKind.String
           && DateTime.TryParse(v.GetString(), null, System.Globalization.DateTimeStyles.AdjustToUniversal
               | System.Globalization.DateTimeStyles.AssumeUniversal, out var at)
            ? at : null;

    // The id is part of the materializer's canonical path, not inferred from a basename or a
    // neighbouring turn. Content still goes through the session Files API's access checks.
    [GeneratedRegex(@"(?:^|[\s\[\""'])/[A-Za-z0-9._/-]*/\.agenthub/files/([a-f0-9]{32})/([A-Za-z0-9._-]+)(?=$|[\s\]\""'])")]
    private static partial Regex UploadedImagePath();

    private static IReadOnlyList<TranscriptAttachment> PathAttachments(string text) =>
        UploadedImagePath().Matches(text).Cast<Match>()
            .Where(match => new[] { ".png", ".jpg", ".jpeg", ".webp", ".gif" }
                .Contains(System.IO.Path.GetExtension(match.Groups[2].Value), StringComparer.OrdinalIgnoreCase))
            .Select(match => new TranscriptAttachment(match.Groups[1].Value, match.Groups[2].Value))
            .DistinctBy(file => file.Id).Take(5).ToArray();

    private static TranscriptEntry WithAttachments(TranscriptEntry entry, IReadOnlyList<TranscriptAttachment> files)
    {
        var all = (entry.Attachments ?? []).Concat(files).DistinctBy(file => file.Id).Take(5).ToArray();
        return all.Length == 0 ? entry : entry with { Attachments = all };
    }

    private static string Clip(string text)
        => text.Length <= MaxToolChars ? text : text[..MaxToolChars] + "…";
}
