using System.Formats.Tar;
using System.IO.Compression;
using System.Reflection;
using System.Text;
using System.Text.Json.Nodes;
using AgentHub.Api.Controllers;
using AgentHub.Api.Models;
using AgentHub.Api.Persistence;
using AgentHub.Api.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Xunit;

namespace AgentHub.Api.Tests;

/// <summary>
/// Taking a session's conversation to a workstation and back (#453, docs/session-transfer.md).
/// Archives are built the way the pod's GNU tar writes them — GNU format, long names for sidecar
/// paths — so the code is tested against the layout it meets in production.
/// </summary>
public class SessionLocalTransferTests
{
    private const string Owner = "alice";
    private const string SessionId = "s1";
    private const string ResumeId = "11111111-2222-4333-8444-555555555555";
    private const string Older = "99999999-2222-4333-8444-555555555555";
    private const string PodDir = "/workspace/repo";
    private const string PodFolder = "-workspace-repo";
    private const string LocalDir = "/home/me/work/my.project";
    private const string LocalFolder = "-home-me-work-my-project";

    // ------------------------------------------------------------------ the archive

    [Fact]
    public void ForLocal_files_the_conversation_under_the_local_folder_and_rewrites_only_cwd()
    {
        var result = SessionLocalTransfer.ForLocal(PodState(), LocalDir);

        Assert.Equal(ResumeId, result.ResumeId);
        Assert.Equal(LocalFolder, result.ProjectFolder);
        var files = Entries(result.Archive);
        Assert.Equal(
            [$".claude/projects/{LocalFolder}/{ResumeId}.jsonl",
             $".claude/projects/{LocalFolder}/{ResumeId}/subagents/agent-{new string('a', 80)}.jsonl"],
            files.Keys.Order());
        var lines = files[$".claude/projects/{LocalFolder}/{ResumeId}.jsonl"].Split('\n');
        Assert.Equal(LocalDir, JsonNode.Parse(lines[1])!["cwd"]!.GetValue<string>());
        // Untouched: the session id, the non-ASCII text (no \u escapes), and the cut-off last line.
        Assert.Contains($"\"sessionId\":\"{ResumeId}\"", lines[1]);
        Assert.Contains("Grüße <b>&</b>", lines[1]);
        Assert.Equal("{\"type\":\"user\",\"cwd\":\"/workspace/re", lines[^1]);
        // The other conversation, the settings and the credentials stay in the hub.
        Assert.DoesNotContain(files.Keys, name => name.Contains(Older) || name.Contains("settings"));
    }

    [Fact]
    public void ForLocal_prefers_the_driver_marker_over_the_newest_transcript()
    {
        // After a context clear the pod resumes the id in the marker; the other transcript may be
        // newer on disk (a subagent finished writing it last), and resuming that one would be wrong.
        var result = SessionLocalTransfer.ForLocal(PodState(olderIsNewest: true), LocalDir);
        Assert.Equal(ResumeId, result.ResumeId);
    }

    [Fact]
    public void ForLocal_without_a_marker_takes_the_newest_transcript()
    {
        var result = SessionLocalTransfer.ForLocal(PodState(marker: false, olderIsNewest: true), LocalDir);
        Assert.Equal(Older, result.ResumeId);
    }

    [Fact]
    public void ForLocal_refuses_state_without_a_conversation()
    {
        var empty = Gzip(Gnu((".claude/settings.json", "{}", default)));
        var error = Assert.Throws<SessionTransferException>(() => SessionLocalTransfer.ForLocal(empty, LocalDir));
        Assert.Equal("transcript_missing", error.Code);
    }

    [Theory]
    [InlineData("relative/dir")]
    [InlineData("")]
    [InlineData("~/project")]
    [InlineData("/home/me/\nproject")]
    public void Local_directory_must_be_absolute(string directory)
        => Assert.Equal("directory_invalid",
            Assert.Throws<SessionTransferException>(() => SessionLocalTransfer.ValidateLocalDirectory(directory)).Code);

    [Theory]
    [InlineData("/home/me/project/", "/home/me/project", "-home-me-project")]
    [InlineData("C:\\Users\\Me\\GIT\\agenthub", "C:\\Users\\Me\\GIT\\agenthub", "C--Users-Me-GIT-agenthub")]
    [InlineData("C:\\", "C:\\", "C--")]
    public void Local_directory_and_folder_match_what_claude_code_derives(string input, string directory, string folder)
    {
        Assert.Equal(directory, SessionLocalTransfer.ValidateLocalDirectory(input));
        Assert.Equal(folder, SessionLocalTransfer.ProjectFolder(directory));
    }

    [Fact]
    public void Round_trip_stores_the_hub_lines_byte_for_byte_and_appends_the_local_turns()
    {
        var original = PodState();
        var local = SessionLocalTransfer.ForLocal(Copy(original), LocalDir);
        // What `claude --resume` on the laptop does: append turns that carry the local cwd.
        var localFiles = Entries(local.Archive);
        var transcriptName = $".claude/projects/{LocalFolder}/{ResumeId}.jsonl";
        var continued = localFiles[transcriptName].TrimEnd('\n') + "\n"
            + $"{{\"type\":\"user\",\"uuid\":\"u-local\",\"cwd\":\"{LocalDir}\",\"sessionId\":\"{ResumeId}\",\"message\":\"from the laptop\"}}\n";
        var upload = Gzip(Gnu((transcriptName, continued, default)));

        var merged = Entries(SessionLocalTransfer.MergeFromLocal(Copy(original), upload));

        var stored = merged[$".claude/projects/{PodFolder}/{ResumeId}.jsonl"];
        var hub = Entries(Copy(original).ToArray())[$".claude/projects/{PodFolder}/{ResumeId}.jsonl"];
        // The complete JSON lines the pod wrote come back unchanged, so the resuming pod's
        // catch-up (#146) sees its hub copy as a prefix rather than "a different history".
        var hubComplete = hub[..(hub.LastIndexOf('\n') + 1)];
        Assert.StartsWith(hubComplete, stored);
        Assert.Contains($"\"cwd\":\"{PodDir}\"", stored[hubComplete.Length..]);
        Assert.Contains("from the laptop", stored);
        Assert.DoesNotContain(LocalDir, stored);
        // Everything else in the home directory is as the pod left it.
        Assert.Equal("{\"theme\":\"dark\"}", merged[".claude/settings.json"]);
        Assert.True(merged.ContainsKey($".claude/projects/{PodFolder}/{Older}.jsonl"));
    }

    [Fact]
    public void Hand_back_renames_a_forked_local_conversation_to_the_id_the_pod_resumes()
    {
        const string forked = "abcdefab-2222-4333-8444-555555555555";
        var upload = Gzip(Gnu(($".claude/projects/{LocalFolder}/{forked}.jsonl",
            $"{{\"uuid\":\"u-first\",\"cwd\":\"{LocalDir}\",\"sessionId\":\"{forked}\"}}\n", default)));

        var merged = Entries(SessionLocalTransfer.MergeFromLocal(PodState(), upload));

        var stored = merged[$".claude/projects/{PodFolder}/{ResumeId}.jsonl"];
        Assert.Contains($"\"sessionId\":\"{ResumeId}\"", stored);
        Assert.DoesNotContain(merged.Keys, name => name.Contains(forked));
        Assert.DoesNotContain(merged.Keys, name => name.Contains("subagents"));
    }

    [Fact]
    public void Hand_back_refuses_a_conversation_that_does_not_continue_the_session()
    {
        var upload = Gzip(Gnu(($".claude/projects/{LocalFolder}/{ResumeId}.jsonl",
            "{\"uuid\":\"someone-else\",\"cwd\":\"/x\"}\n", default)));
        Assert.Equal("transcript_unrelated",
            Assert.Throws<SessionTransferException>(() => SessionLocalTransfer.MergeFromLocal(PodState(), upload)).Code);
    }

    [Theory]
    [InlineData(".claude/settings.json")]
    [InlineData(".claude/projects/x/../../../.bashrc")]
    [InlineData(".bashrc")]
    [InlineData(".claude/projects/x/memory/notes.md")]
    public void Hand_back_refuses_anything_but_one_conversation(string extra)
    {
        var upload = Gzip(Gnu(
            ($".claude/projects/{LocalFolder}/{ResumeId}.jsonl", "{\"uuid\":\"u-first\"}\n", default),
            (extra, "x", default)));
        Assert.Equal("upload_invalid",
            Assert.Throws<SessionTransferException>(() => SessionLocalTransfer.MergeFromLocal(PodState(), upload)).Code);
    }

    [Fact]
    public void Hand_back_refuses_a_symlink_and_a_non_archive()
    {
        using var buffer = new MemoryStream();
        using (var zip = new GZipStream(buffer, CompressionLevel.Fastest, leaveOpen: true))
        using (var tar = new TarWriter(zip, TarEntryFormat.Gnu, leaveOpen: true))
            tar.WriteEntry(new GnuTarEntry(TarEntryType.SymbolicLink, $".claude/projects/{LocalFolder}/{ResumeId}.jsonl")
                { LinkName = "/etc/passwd" });
        Assert.Equal("upload_invalid", Assert.Throws<SessionTransferException>(
            () => SessionLocalTransfer.MergeFromLocal(PodState(), new MemoryStream(buffer.ToArray()))).Code);
        Assert.Equal("upload_invalid", Assert.Throws<SessionTransferException>(
            () => SessionLocalTransfer.MergeFromLocal(PodState(), new MemoryStream("not gzip"u8.ToArray()))).Code);
    }

    // ------------------------------------------------------------------ the rules

    [Fact]
    public async Task Take_local_of_a_live_session_does_nothing_without_confirmation()
    {
        var sessions = new Sessions(SessionStatus.Running);
        var result = await Service(sessions).TakeLocalAsync(Owner, SessionId, LocalDir, confirm: false, withLink: false);

        Assert.True(result.ConfirmationRequired);
        Assert.Contains(result.Steps, step => step.StartsWith("Pause the session"));
        Assert.Equal(0, sessions.Pauses);
        Assert.Null(result.ResumeId);
    }

    [Fact]
    public async Task Take_local_with_confirmation_pauses_and_waits_for_the_pod_before_reading()
    {
        var sessions = new Sessions(SessionStatus.Running);
        var result = await Service(sessions).TakeLocalAsync(Owner, SessionId, LocalDir, confirm: true, withLink: false);

        Assert.True(result.Paused);
        Assert.Equal(1, sessions.Pauses);
        Assert.Equal(["pause", "wait", "read"], sessions.Calls.Where(call => call is "pause" or "wait" or "read").Take(3));
        Assert.Equal(ResumeId, result.ResumeId);
        Assert.Equal($"claude --resume {ResumeId}", result.ResumeCommand);
    }

    [Fact]
    public async Task Download_and_hand_back_are_refused_next_to_a_live_pod()
    {
        var service = Service(new Sessions(SessionStatus.Running));
        Assert.Equal("session_live", (await Assert.ThrowsAsync<SessionTransferException>(
            () => service.LocalArchiveAsync(Owner, SessionId, LocalDir))).Code);
        Assert.Equal("session_live", (await Assert.ThrowsAsync<SessionTransferException>(
            () => service.HandBackAsync(Owner, SessionId, Stream.Null, resume: true))).Code);
        Assert.Equal("session_live", (await Assert.ThrowsAsync<SessionTransferException>(
            () => service.HandBackLinkAsync(Owner, SessionId, LocalDir, resume: true))).Code);
    }

    [Fact]
    public async Task Other_agents_are_refused()
    {
        var service = Service(new Sessions(SessionStatus.Paused, AgentKind.Codex));
        Assert.Equal("agent_not_supported", (await Assert.ThrowsAsync<SessionTransferException>(
            () => service.TakeLocalAsync(Owner, SessionId, LocalDir, confirm: true, withLink: false))).Code);
    }

    [Fact]
    public async Task Hand_back_stores_the_merged_state_and_resumes()
    {
        var sessions = new Sessions(SessionStatus.Paused);
        var local = SessionLocalTransfer.ForLocal(PodState(), LocalDir);

        using var upload = new MemoryStream(local.Archive);
        var result = await Service(sessions).HandBackAsync(Owner, SessionId, upload, resume: true);

        Assert.True(result.Resumed);
        Assert.Equal(1, sessions.Resumes);
        Assert.True(Entries(sessions.State).ContainsKey($".claude/projects/{PodFolder}/{ResumeId}.jsonl"));
    }

    [Fact]
    public async Task Links_need_a_configured_origin_and_never_use_the_request_host()
    {
        var service = Service(new Sessions(SessionStatus.Paused), origin: null);
        Assert.Equal("origin_not_configured", (await Assert.ThrowsAsync<SessionTransferException>(
            () => service.TakeLocalAsync(Owner, SessionId, LocalDir, confirm: true, withLink: true))).Code);
    }

    [Fact]
    public async Task Take_local_link_carries_commands_that_quote_the_directory()
    {
        var result = await Service(new Sessions(SessionStatus.Paused))
            .TakeLocalAsync(Owner, SessionId, "/home/me/it's here", confirm: false, withLink: true);

        var link = result.Link!;
        Assert.StartsWith("https://hub.example.com/api/session-transfer/oat_", link.Url);
        Assert.Contains("mkdir -p '/home/me/it'\\''s here'", link.Command);
        Assert.Contains("tar -xzkf - -C \"$HOME\"", link.Command);
        Assert.Contains("Set-Location '/home/me/it''s here'", link.PowerShellCommand);
        Assert.EndsWith($"claude --resume {ResumeId}", link.Command);
    }

    // ------------------------------------------------------------------ the endpoints

    [Fact]
    public async Task A_download_link_works_once_and_then_answers_like_an_unknown_one()
    {
        var sessions = new Sessions(SessionStatus.Paused);
        var tickets = new MemoryTickets();
        var service = Service(sessions, tickets: tickets);
        var taken = await service.TakeLocalAsync(Owner, SessionId, LocalDir, confirm: false, withLink: true);
        var secret = taken.Link!.Url.Split('/')[^1];
        var controller = Controller(service);

        var first = Assert.IsType<FileContentResult>(await controller.LinkDownload(secret, CancellationToken.None));
        Assert.Equal(ResumeId, controller.Response.Headers["X-AgentHub-Resume-Id"].ToString());
        Assert.True(Entries(first.FileContents).ContainsKey($".claude/projects/{LocalFolder}/{ResumeId}.jsonl"));

        var again = Assert.IsType<NotFoundObjectResult>(await Controller(service).LinkDownload(secret, CancellationToken.None));
        var unknown = Assert.IsType<NotFoundObjectResult>(await Controller(service).LinkDownload("oat_nope", CancellationToken.None));
        Assert.Equivalent(again.Value, unknown.Value);
        // An upload link does not download, and the reverse.
        Assert.IsType<NotFoundObjectResult>(await Controller(service).LinkUpload(secret, CancellationToken.None));
    }

    [Fact]
    public async Task Token_endpoints_reject_a_missing_token()
    {
        var controller = Controller(Service(new Sessions(SessionStatus.Paused)));
        Assert.IsType<UnauthorizedResult>(await controller.RemoteStatus(SessionId, CancellationToken.None));
        Assert.IsType<UnauthorizedResult>(await controller.RemoteLocalArchive(SessionId, LocalDir, CancellationToken.None));
    }

    [Fact]
    public void Web_app_actions_require_a_login_and_only_token_and_link_actions_are_anonymous()
    {
        // [AllowAnonymous] on the class would silently override [Authorize] on these actions.
        Assert.Null(typeof(SessionTransferController).GetCustomAttribute<AllowAnonymousAttribute>());
        foreach (var method in typeof(SessionTransferController).GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly))
        {
            var route = method.GetCustomAttributes().OfType<Microsoft.AspNetCore.Mvc.Routing.HttpMethodAttribute>().Single().Template!;
            var anonymous = method.GetCustomAttribute<AllowAnonymousAttribute>() is not null;
            var authorized = method.GetCustomAttribute<AuthorizeAttribute>() is not null;
            if (route.StartsWith("api/sessions/")) Assert.True(authorized && !anonymous, route);
            else Assert.True(anonymous && !authorized, route);
        }
    }

    [Fact]
    public async Task Remote_mcp_tools_answer_with_a_link_and_ask_before_pausing()
    {
        var sessions = new Sessions(SessionStatus.Running);
        var http = new HttpContextAccessor { HttpContext = new DefaultHttpContext() };
        http.HttpContext.User = new System.Security.Claims.ClaimsPrincipal(new System.Security.Claims.ClaimsIdentity(
            [new System.Security.Claims.Claim("preferred_username", Owner)], "test"));
        var tools = new AgentHub.Api.Mcp.AgentHubMcpTools(http, sessions, NullLogger<AgentHub.Api.Mcp.AgentHubMcpTools>.Instance,
            transfer: Service(sessions));

        var asked = await tools.TakeLocal(SessionId, LocalDir);
        Assert.True(asked.ConfirmationRequired);
        Assert.Equal(0, sessions.Pauses);

        var taken = await tools.TakeLocal(SessionId, LocalDir, confirm: "true");
        Assert.Equal(1, sessions.Pauses);
        Assert.NotNull(taken.Link);

        var back = await tools.HandBack(SessionId, LocalDir);
        Assert.Contains($".claude/projects/{LocalFolder}/{ResumeId}.jsonl", back.Link.Command);
    }

    // ------------------------------------------------------------------ Postgres

    [PostgreSqlFact]
    public async Task Tickets_are_single_use_bound_to_one_direction_and_expire()
    {
        await using var db = await TicketDatabase.CreateAsync();
        var store = db.Store;
        var ticket = new SessionTransferTicket(Owner, SessionId, SessionTransferDirection.HandBack, LocalDir, true,
            DateTimeOffset.UtcNow.AddMinutes(15));

        var secret = await store.MintAsync(ticket);
        Assert.StartsWith("oat_", secret);
        Assert.Null(await store.RedeemAsync(secret, SessionTransferDirection.TakeLocal));
        var redeemed = await store.RedeemAsync(secret, SessionTransferDirection.HandBack);
        Assert.Equal((Owner, SessionId, LocalDir, true), (redeemed!.Owner, redeemed.SessionId, redeemed.Directory, redeemed.Resume));
        Assert.Null(await store.RedeemAsync(secret, SessionTransferDirection.HandBack));

        var expired = await store.MintAsync(ticket with { ExpiresAt = DateTimeOffset.UtcNow.AddSeconds(-1) });
        Assert.Null(await store.RedeemAsync(expired, SessionTransferDirection.HandBack));
        // The secret itself is never stored.
        Assert.Equal(0, await db.CountAsync($"SELECT count(*) FROM session_transfer_tickets WHERE secret_hash = '{secret}'"));
    }

    [PostgreSqlFact]
    public async Task Concurrent_redemptions_of_one_ticket_succeed_once()
    {
        await using var db = await TicketDatabase.CreateAsync();
        var secret = await db.Store.MintAsync(new SessionTransferTicket(Owner, SessionId, SessionTransferDirection.TakeLocal,
            LocalDir, false, DateTimeOffset.UtcNow.AddMinutes(15)));

        var results = await Task.WhenAll(Enumerable.Range(0, 8)
            .Select(_ => db.Store.RedeemAsync(secret, SessionTransferDirection.TakeLocal)));

        Assert.Single(results, result => result is not null);
    }

    // ------------------------------------------------------------------ fixtures

    private static SessionTransferService Service(Sessions sessions, string? origin = "https://hub.example.com/",
        ISessionTransferTicketStore? tickets = null)
    {
        var cfg = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["FrontendOrigin"] = origin }).Build();
        return new SessionTransferService(sessions, tickets ?? new MemoryTickets(), cfg, NullLogger<SessionTransferService>.Instance);
    }

    private static SessionTransferController Controller(SessionTransferService service)
        => new((_, _) => Task.FromResult<RemoteCaller?>(null), service)
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() }
        };

    private static string Line(string uuid, string cwd, string sessionId, string text)
        => $"{{\"parentUuid\":null,\"type\":\"user\",\"uuid\":\"{uuid}\",\"cwd\":\"{cwd}\",\"sessionId\":\"{sessionId}\",\"message\":{{\"content\":\"{text}\"}},\"n\":1.50}}";

    /// <summary>A pod's ~/.claude as its GNU tar packs it.</summary>
    private static MemoryStream PodState(bool marker = true, bool olderIsNewest = false)
    {
        var now = DateTimeOffset.UtcNow;
        var transcript = string.Join('\n',
            "{\"type\":\"permission-mode\",\"permissionMode\":\"default\"}",
            Line("u-first", PodDir, ResumeId, "Grüße <b>&</b>"),
            Line("u-second", PodDir, ResumeId, "second"),
            "{\"type\":\"user\",\"cwd\":\"/workspace/re");
        var files = new List<(string, string, DateTimeOffset)>
        {
            (".claude/settings.json", "{\"theme\":\"dark\"}", now),
            ($".claude/projects/{PodFolder}/{ResumeId}.jsonl", transcript, now.AddMinutes(-5)),
            ($".claude/projects/{PodFolder}/{ResumeId}/subagents/agent-{new string('a', 80)}.jsonl", "{\"cwd\":\"/workspace/repo\"}\n", now),
            ($".claude/projects/{PodFolder}/{Older}.jsonl", Line("u-old", PodDir, Older, "old") + "\n",
                olderIsNewest ? now : now.AddHours(-1))
        };
        if (marker) files.Add((SessionLocalTransfer.SessionIdMarker, ResumeId + "\n", now));
        return Gzip(Gnu([.. files]));
    }

    private static byte[] Gnu(params (string Name, string Content, DateTimeOffset Time)[] files)
    {
        using var buffer = new MemoryStream();
        using (var tar = new TarWriter(buffer, TarEntryFormat.Gnu, leaveOpen: true))
        {
            tar.WriteEntry(new GnuTarEntry(TarEntryType.Directory, ".claude/") { Mode = (UnixFileMode)0b111_101_101 });
            foreach (var (name, content, time) in files)
                tar.WriteEntry(new GnuTarEntry(TarEntryType.RegularFile, name)
                {
                    DataStream = new MemoryStream(Encoding.UTF8.GetBytes(content)),
                    ModificationTime = time == default ? DateTimeOffset.UtcNow : time
                });
        }
        return buffer.ToArray();
    }

    private static MemoryStream Gzip(byte[] tar)
    {
        var output = new MemoryStream();
        using (var zip = new GZipStream(output, CompressionLevel.Fastest, leaveOpen: true)) zip.Write(tar);
        output.Position = 0;
        return output;
    }

    private static MemoryStream Copy(MemoryStream source) => new(source.ToArray());

    private static Dictionary<string, string> Entries(byte[] archive)
    {
        var result = new Dictionary<string, string>();
        using var zip = new GZipStream(new MemoryStream(archive), CompressionMode.Decompress);
        using var tar = new TarReader(zip);
        while (tar.GetNextEntry() is { } entry)
        {
            if (entry.EntryType is not TarEntryType.RegularFile) continue;
            using var reader = new StreamReader(entry.DataStream!, Encoding.UTF8);
            result[entry.Name] = reader.ReadToEnd();
        }
        return result;
    }

    private sealed class MemoryTickets : ISessionTransferTicketStore
    {
        private readonly Dictionary<string, SessionTransferTicket> _tickets = new();

        public Task<string> MintAsync(SessionTransferTicket ticket, CancellationToken ct = default)
        {
            var secret = "oat_" + Guid.NewGuid().ToString("n");
            _tickets[secret] = ticket;
            return Task.FromResult(secret);
        }

        public Task<SessionTransferTicket?> RedeemAsync(string secret, SessionTransferDirection direction, CancellationToken ct = default)
            => Task.FromResult(_tickets.Remove(secret, out var ticket) && ticket.Direction == direction
                                && ticket.ExpiresAt > DateTimeOffset.UtcNow ? ticket : null);
    }

    /// <summary>One owned session whose phase follows pause and resume as the cluster's would.</summary>
    private sealed class Sessions(string phase, AgentKind agent = AgentKind.Claude) : ISessionService
    {
        public string Phase { get; private set; } = phase;
        public byte[] State { get; private set; } = PodState().ToArray();
        public List<string> Calls { get; } = [];
        public int Pauses { get; private set; }
        public int Resumes { get; private set; }

        private SessionInfo Info() => new() { Id = SessionId, Title = "t", Owner = Owner, Mode = SessionMode.Interactive, Phase = Phase, Agent = agent };

        public Task<SessionInfo?> GetSessionAsync(string o, string id, CancellationToken ct = default)
            => Task.FromResult(o == Owner && id == SessionId ? Info() : null);

        public Task<SessionInfo> PauseSessionAsync(string o, string id, CancellationToken ct = default)
        {
            Calls.Add("pause");
            Pauses++;
            Phase = SessionStatus.Paused;
            return Task.FromResult(Info());
        }

        public Task WaitForPodGoneAsync(string owner, string id, CancellationToken ct = default)
        {
            Calls.Add("wait");
            return Task.CompletedTask;
        }

        public Task<SessionInfo> ResumeSessionAsync(string o, string id, CancellationToken ct = default)
        {
            Resumes++;
            Phase = SessionStatus.Pending;
            return Task.FromResult(Info());
        }

        public Task<Stream?> OpenStateArchiveAsync(string o, string id, CancellationToken ct = default)
        {
            Calls.Add("read");
            return Task.FromResult<Stream?>(o == Owner && id == SessionId ? new MemoryStream(State) : null);
        }

        public async Task<bool> ReplaceStateArchiveAsync(string o, string id, Stream content, long? contentLength, CancellationToken ct = default)
        {
            if (!SessionStatus.CanReplaceState(Phase)) throw new InvalidOperationException("live");
            using var buffer = new MemoryStream();
            await content.CopyToAsync(buffer, ct);
            State = buffer.ToArray();
            return true;
        }

        public Task StoreCredentialsAsync(string o, UserCredentials creds, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<CredentialStatus> GetCredentialStatusAsync(string o, CancellationToken ct = default) => throw new NotSupportedException();
        public Task DeleteProviderCredentialsAsync(string owner, AgentKind agent, CancellationToken ct = default) => throw new NotSupportedException();
        public Task StoreProviderCredentialsAsync(string o, AgentKind a, string json, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<SessionInfo> CreateSessionAsync(string o, CreateSessionRequest req, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<SessionInfo> DuplicateSessionAsync(string o, string id, DuplicateSessionRequest req, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<SessionInfo> UpdateSessionAsync(string o, string id, UpdateSessionRequest req, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<SessionInfo>> ListSessionsAsync(string o, CancellationToken ct = default) => throw new NotSupportedException();
        public Task ClearQuestionAsync(string o, string id, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<string?> GetTranscriptAsync(string o, string id, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<string?> MintArtifactUploadUrlAsync(string id, string token, string name, CancellationToken ct = default) => throw new NotSupportedException();
        public Task DeleteSessionAsync(string o, string id, CancellationToken ct = default) => throw new NotSupportedException();
    }

    /// <summary>A schema of its own per test, without pooling — see the CI connection limit (#366).</summary>
    private sealed class TicketDatabase : IAsyncDisposable
    {
        private readonly string _admin;
        private readonly string _schema;
        private readonly string _scoped;
        public PostgresSessionTransferTicketStore Store { get; }

        private TicketDatabase(string admin, string schema)
        {
            _admin = admin;
            _schema = schema;
            _scoped = new NpgsqlConnectionStringBuilder(admin) { SearchPath = schema, Pooling = false }.ConnectionString;
            Store = new PostgresSessionTransferTicketStore(new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?> { ["ConnectionStrings:Postgres"] = _scoped }).Build());
        }

        public static async Task<TicketDatabase> CreateAsync()
        {
            var admin = Environment.GetEnvironmentVariable(PostgreSqlFactAttribute.ConnectionStringEnvironmentVariable)!;
            var db = new TicketDatabase(admin, "transfer_" + Guid.NewGuid().ToString("n"));
            await db.ExecAsync(admin, $"CREATE SCHEMA {db._schema}");
            await db.Store.InitializeAsync();
            return db;
        }

        public async Task<long> CountAsync(string sql)
        {
            await using var connection = new NpgsqlConnection(_scoped);
            await connection.OpenAsync();
            await using var command = new NpgsqlCommand(sql, connection);
            return (long)(await command.ExecuteScalarAsync())!;
        }

        private async Task ExecAsync(string cs, string sql)
        {
            await using var connection = new NpgsqlConnection(new NpgsqlConnectionStringBuilder(cs) { Pooling = false }.ConnectionString);
            await connection.OpenAsync();
            await using var command = new NpgsqlCommand(sql, connection);
            await command.ExecuteNonQueryAsync();
        }

        public async ValueTask DisposeAsync() => await ExecAsync(_admin, $"DROP SCHEMA IF EXISTS {_schema} CASCADE");
    }
}
