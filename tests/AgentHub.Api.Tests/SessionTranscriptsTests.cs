using System.Text;
using AgentHub.Api.Controllers;
using AgentHub.Api.Models;
using AgentHub.Api.Persistence;
using AgentHub.Api.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Xunit;

namespace AgentHub.Api.Tests;

/// <summary>
/// Which text each reader gets: the provider's own conversation when one was uploaded, the
/// cleaned scrollback otherwise — and the same answer on every surface.
/// </summary>
public class SessionTranscriptsTests
{
    private static readonly IReadOnlyList<TranscriptEntry> Conversation =
    [
        new("user", "do it", null),
        new("assistant", "done", null)
    ];

    [Fact]
    public async Task Readable_PrefersTheNativeConversationOverTheScrollback()
    {
        var svc = new TranscriptService { Entries = Conversation, Scrollback = "raw terminal noise" };

        var text = await SessionTranscripts.ReadableAsync(svc, "alice", "s1", CancellationToken.None);

        Assert.Equal("## User\ndo it\n\n## Assistant\ndone\n", text);
        Assert.Equal(0, svc.ScrollbackReads);
    }

    [Fact]
    public async Task Readable_FallsBackToTheScrollbackWithoutOne()
    {
        var svc = new TranscriptService { Entries = null, Scrollback = "terminal output" };

        Assert.Equal("terminal output", await SessionTranscripts.ReadableAsync(svc, "alice", "s1", CancellationToken.None));
        Assert.Null(await SessionTranscripts.ReadableAsync(
            new TranscriptService { Entries = null, Scrollback = null }, "alice", "s1", CancellationToken.None));
    }

    [Fact]
    public async Task Page_SaysWhichSourceItCameFrom()
    {
        var native = await SessionTranscripts.PageAsync(
            new TranscriptService { Entries = Conversation }, "alice", "s1", "Running", null, null, CancellationToken.None);
        Assert.Equal("native", native!.Source);
        Assert.Equal(2, native.Entries.Count);

        var fallback = await SessionTranscripts.PageAsync(
            new TranscriptService { Entries = null, Scrollback = "text" }, "alice", "s1", "Succeeded", null, null, CancellationToken.None);
        Assert.Equal("scrollback", fallback!.Source);
        Assert.Equal("text", fallback.Text);
        Assert.False(fallback.Running);
    }

    [Fact]
    public async Task RemoteApiAndMcpTools_ServeTheConversationNotTheScrollback()
    {
        var svc = new TranscriptService { Entries = Conversation, Scrollback = "{\"type\":\"assistant\"} stream json" };

        var remote = new RemoteController((_, _) => Task.FromResult<string?>("alice"), svc)
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() }
        };
        remote.Request.Headers.Authorization = "Bearer oah_token";
        var page = Assert.IsType<TranscriptPage>(
            Assert.IsType<OkObjectResult>((await remote.Transcript("s1", null, null, CancellationToken.None)).Result).Value);
        Assert.StartsWith("## User\ndo it", page.Text);

        var tools = new AgentHub.Api.Mcp.AgentHubMcpTools(
            new HttpContextAccessor { HttpContext = new DefaultHttpContext { User = Alice() } }, svc,
            Microsoft.Extensions.Logging.Abstractions.NullLogger<AgentHub.Api.Mcp.AgentHubMcpTools>.Instance);
        Assert.StartsWith("## User\ndo it", await tools.GetSessionLogs("s1"));
        Assert.StartsWith("## User\ndo it", (await tools.GetSessionTranscript("s1")).Text);
    }

    [Fact]
    public async Task WebApp_ConversationEndpoint_PagesByCursorAndReportsThePhase()
    {
        var svc = new TranscriptService { Entries = Conversation, Phase = "Running" };
        var controller = new SessionsController(svc, null!, [])
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext { User = Alice() } }
        };

        var first = Assert.IsType<ConversationPage>(
            Assert.IsType<OkObjectResult>((await controller.Conversation("s1", null, 1, CancellationToken.None)).Result).Value);
        Assert.Equal("Running", first.Phase);
        Assert.Single(first.Entries);
        Assert.Equal(1, first.NextOffset);

        var next = Assert.IsType<ConversationPage>(
            Assert.IsType<OkObjectResult>((await controller.Conversation("s1", first.NextOffset, null, CancellationToken.None)).Result).Value);
        Assert.Equal("done", Assert.Single(next.Entries).Text);

        svc.Phase = null;
        Assert.IsType<NotFoundResult>((await controller.Conversation("s1", null, null, CancellationToken.None)).Result);
    }

    [Fact]
    public async Task Upload_StoresTheCappedTailOfTheNativeTranscript()
    {
        var store = new RecordingStore();
        var controller = new InternalController(store, [], new TranscriptService(), null!, [], [], null!, null!)
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() }
        };
        controller.Request.Headers["X-Agent-Token"] = "tok";
        var line = "{\"n\":" + new string('1', ScrollbackLimits.MaxChars / 2) + "}\n";
        controller.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes(line + line + "{\"last\":true}\n"));

        Assert.IsType<NoContentResult>(await controller.Transcript("s1", CancellationToken.None));

        Assert.True(store.Transcript!.Length <= ScrollbackLimits.MaxChars);
        Assert.StartsWith("{\"", store.Transcript);
        Assert.EndsWith("{\"last\":true}\n", store.Transcript);
    }

    // ------------------------------------------------------------------ fixtures

    private static System.Security.Claims.ClaimsPrincipal Alice() =>
        new(new System.Security.Claims.ClaimsIdentity(
            [new System.Security.Claims.Claim("preferred_username", "alice")], "test"));

    private sealed class TranscriptService : RecordingWebhookSessionService
    {
        public IReadOnlyList<TranscriptEntry>? Entries { get; init; }
        public string? Scrollback { get; init; }
        public string? Phase { get; set; } = "Running";
        public int ScrollbackReads { get; private set; }

        public override Task<IReadOnlyList<TranscriptEntry>?> GetConversationAsync(string owner, string id, CancellationToken ct = default)
            => Task.FromResult(Entries);

        public override Task<string?> GetTranscriptAsync(string owner, string id, CancellationToken ct = default)
        {
            ScrollbackReads++;
            return Task.FromResult(Scrollback);
        }

        public override Task<SessionInfo?> GetSessionAsync(string owner, string id, CancellationToken ct = default)
            => Task.FromResult(Phase is null ? null : new SessionInfo
            {
                Id = id, Owner = owner, Title = "t", Mode = SessionMode.Interactive, Phase = Phase
            });
    }

    private sealed class RecordingStore : ISessionStore
    {
        private readonly SessionRecord _session = new()
        {
            Id = "s1", Owner = "alice", CallbackToken = "tok", Mode = SessionMode.Interactive, Title = "s"
        };

        public string? Transcript { get; private set; }

        public Task InitializeAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task UpsertAsync(SessionRecord r, CancellationToken ct = default) => Task.CompletedTask;
        public Task<SessionRecord?> GetAsync(string owner, string id, CancellationToken ct = default) =>
            Task.FromResult(owner == _session.Owner && id == _session.Id ? _session : null);
        public Task<SessionRecord?> GetByCallbackTokenAsync(string token, CancellationToken ct = default) =>
            Task.FromResult(token == _session.CallbackToken ? _session : null);
        public Task<IReadOnlyList<SessionRecord>> ListAsync(string owner, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<SessionRecord>>([]);
        public Task UpdateStatusAsync(string id, string status, CancellationToken ct = default) => Task.CompletedTask;
        public Task SetQuestionPendingAsync(string id, bool pending, CancellationToken ct = default) => Task.CompletedTask;
        public Task SetScrollbackAsync(string id, string text, CancellationToken ct = default) => Task.CompletedTask;
        public Task<string?> GetScrollbackAsync(string id, CancellationToken ct = default) => Task.FromResult<string?>(null);
        public Task SetTranscriptAsync(string id, string jsonl, CancellationToken ct = default)
        {
            Transcript = jsonl;
            return Task.CompletedTask;
        }
        public Task DeleteAsync(string id, CancellationToken ct = default) => Task.CompletedTask;
    }
}
