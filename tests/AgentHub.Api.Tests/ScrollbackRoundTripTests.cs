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
/// The scrollback round trip between the session agent and the hub: what the agent uploads is
/// what a resuming agent gets back, byte for byte, and every cap along the way is the same one.
/// </summary>
public class ScrollbackRoundTripTests
{
    private const string SessionId = "sess-1";
    private const string Token = "callback-token";
    private const string Owner = "alice";
    private const string Esc = "\u001b";

    [Fact]
    public void EveryCapIsTheSameConstant()
    {
        // The agent keeps 1 MB (MAX_BUFFER in server.js); the hub must accept and page as much,
        // or a resume comes back shorter than what the pod uploaded.
        Assert.Equal(1_000_000, ScrollbackLimits.MaxChars);
        Assert.Equal(ScrollbackLimits.MaxChars, TranscriptPage.MaxChars);
    }

    [Fact]
    public async Task Upload_KeepsTheTailUpToTheSharedCap()
    {
        var store = new RecordingStore();
        var controller = Controller(store, new ScrollbackService(null));
        var text = "head-" + new string('x', ScrollbackLimits.MaxChars);
        controller.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes(text));

        var result = await controller.Scrollback(SessionId, CancellationToken.None);

        Assert.IsType<NoContentResult>(result);
        Assert.Equal(ScrollbackLimits.MaxChars, store.Scrollback!.Length);
        Assert.DoesNotContain("head-", store.Scrollback);
    }

    [Fact]
    public async Task Upload_BelowTheCap_IsStoredUnchanged()
    {
        var store = new RecordingStore();
        var controller = Controller(store, new ScrollbackService(null));
        var raw = Esc + "[31mred" + Esc + "[0m\r\n";
        controller.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes(raw));

        await controller.Scrollback(SessionId, CancellationToken.None);

        Assert.Equal(raw, store.Scrollback);
    }

    [Fact]
    public async Task ResumeSeed_IsTheRawScrollbackNotTheStrippedTranscript()
    {
        // The agent replays this into a terminal and persists it again as its own scrollback.
        // Stripped text would lose colours and carriage returns for good on the first resume.
        var raw = Esc + "[31mred" + Esc + "[0m\r\nnext";
        var controller = Controller(new RecordingStore(), new ScrollbackService(raw));

        var result = await controller.GetScrollback(SessionId, CancellationToken.None);

        var content = Assert.IsType<ContentResult>(result);
        Assert.Equal(raw, content.Content);
        Assert.Equal("text/plain", content.ContentType);
    }

    [Fact]
    public async Task ResumeSeed_WithWrongToken_IsRefused()
    {
        var controller = Controller(new RecordingStore(), new ScrollbackService("secret"), token: "wrong");

        Assert.IsType<UnauthorizedResult>(await controller.GetScrollback(SessionId, CancellationToken.None));
    }

    [Fact]
    public async Task Reader_RawKeepsControlSequences_CleanStripsThem()
    {
        var raw = Esc + "[1mbold" + Esc + "[0m\r\n";
        var fromRaw = await TranscriptReader.ReadRawAsync(
            _ => Task.FromResult<string?>(raw), _ => Task.FromResult<string?>("fallback"));
        var cleaned = await TranscriptReader.ReadAsync(
            _ => Task.FromResult<string?>(raw), _ => Task.FromResult<string?>("fallback"));

        Assert.Equal(raw, fromRaw);
        Assert.Equal("bold\n", cleaned);
    }

    // ------------------------------------------------------------------ fixtures

    private static InternalController Controller(RecordingStore store, ISessionService svc, string token = Token)
    {
        var controller = new InternalController(store, [], svc, null!, [], [], null!, null!)
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() }
        };
        controller.Request.Headers["X-Agent-Token"] = token;
        return controller;
    }

    private sealed class ScrollbackService(string? raw) : RecordingWebhookSessionService
    {
        public override Task<string?> GetScrollbackAsync(string owner, string id, CancellationToken ct = default)
            => Task.FromResult(owner == Owner && id == SessionId ? raw : null);
    }

    private sealed class RecordingStore : ISessionStore
    {
        private readonly SessionRecord _session = new()
        {
            Id = SessionId, Owner = Owner, CallbackToken = Token, Mode = SessionMode.Interactive, Title = "s"
        };

        public string? Scrollback { get; private set; }

        public Task InitializeAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task UpsertAsync(SessionRecord r, CancellationToken ct = default) => Task.CompletedTask;
        public Task<SessionRecord?> GetAsync(string owner, string id, CancellationToken ct = default) =>
            Task.FromResult(owner == _session.Owner && id == _session.Id ? _session : null);
        public Task<SessionRecord?> GetByIdAsync(string id, CancellationToken ct = default) =>
            Task.FromResult(id == _session.Id ? _session : null);
        public Task<SessionRecord?> GetByCallbackTokenAsync(string token, CancellationToken ct = default) =>
            Task.FromResult(token == _session.CallbackToken ? _session : null);
        public Task<IReadOnlyList<SessionRecord>> ListAsync(string owner, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<SessionRecord>>([]);
        public Task UpdateStatusAsync(string id, string status, CancellationToken ct = default) => Task.CompletedTask;
        public Task SetQuestionPendingAsync(string id, bool pending, CancellationToken ct = default) => Task.CompletedTask;
        public Task SetScrollbackAsync(string id, string text, CancellationToken ct = default)
        {
            Scrollback = text;
            return Task.CompletedTask;
        }
        public Task<string?> GetScrollbackAsync(string id, CancellationToken ct = default) => Task.FromResult(Scrollback);
        public Task DeleteAsync(string id, CancellationToken ct = default) => Task.CompletedTask;
    }
}
