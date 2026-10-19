using AgentHub.Api.Chat;
using AgentHub.Api.Controllers;
using AgentHub.Api.Models;
using AgentHub.Api.Notifications;
using AgentHub.Api.Persistence;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Xunit;

namespace AgentHub.Api.Tests;

/// <summary>
/// The chat relays open a session's thread only on a "question" event, and the only
/// producer of that event is an agent hook calling <see cref="InternalController.Notify"/>.
/// These tests pin that the endpoint is provider-neutral: a Codex or Cursor hook gets the
/// same fan-out a Claude hook gets, so the thread/binding is created for every runtime
/// that posts here. The chat wording is pinned alongside, since it used to name one
/// provider for all of them.
/// </summary>
public class InternalNotifyTests
{
    [Theory]
    [InlineData("token-s1", true)]
    [InlineData("wrong", false)]
    public async Task TurnStarted_ClearsThePreviousQuestionOnlyWithTheCallbackToken(string token, bool allowed)
    {
        var world = new NotifyWorld(AgentKind.Codex);
        var result = await world.Controller(token).TurnStarted("s1", CancellationToken.None);
        if (allowed)
        {
            Assert.IsType<NoContentResult>(result);
            Assert.Equal([("s1", false)], world.Store.QuestionPending);
        }
        else
        {
            Assert.IsType<UnauthorizedResult>(result);
            Assert.Empty(world.Store.QuestionPending);
        }
        Assert.Empty(world.Notifier.Events);
    }

    [Fact]
    public async Task NextPromptClearsTheOldQuestion_AndANewQuestionRemainsPending()
    {
        var world = new NotifyWorld(AgentKind.Codex);
        var controller = world.Controller();
        await controller.Notify("s1", new InternalController.NotifyBody("Old question", "question"), default);
        await controller.TurnStarted("s1", default);
        await controller.Notify("s1", new InternalController.NotifyBody("New question", "question"), default);

        Assert.Equal([("s1", true), ("s1", false), ("s1", true)], world.Store.QuestionPending);
        Assert.Equal("New question", world.Notifier.Events.Last().Item3);
    }

    [Theory]
    [InlineData(AgentKind.Codex)]
    [InlineData(AgentKind.Cursor)]
    [InlineData(AgentKind.OpenCode)]
    [InlineData(AgentKind.Claude)]
    public async Task Notify_FansOutQuestion_ForAnyAgent(AgentKind agent)
    {
        var world = new NotifyWorld(agent);
        var controller = world.Controller();

        var result = await controller.Notify("s1",
            new InternalController.NotifyBody("Which branch should I use?", "question"), CancellationToken.None);

        Assert.IsType<NoContentResult>(result);
        var (session, ev, message) = Assert.Single(world.Notifier.Events);
        Assert.Equal("question", ev);
        Assert.Equal("Which branch should I use?", message);
        Assert.Equal(agent, session.Agent);
        Assert.Equal(SessionMode.Interactive, session.Mode);
        Assert.Equal([("s1", true)], world.Store.QuestionPending);
    }

    [Fact]
    public async Task Notify_BlankMessage_FallsBackToGenericQuestion()
    {
        // Cursor's stop hook carries no message at all; the thread must still open.
        var world = new NotifyWorld(AgentKind.Cursor);

        await world.Controller().Notify("s1", new InternalController.NotifyBody("  ", null), CancellationToken.None);

        var (_, ev, message) = Assert.Single(world.Notifier.Events);
        Assert.Equal("question", ev);
        Assert.Equal("The agent is waiting for your reply.", message);
    }

    [Fact]
    public async Task Notify_MasksTheSessionsSecretsInWhatTheRelaysPost()
    {
        // Codex's and Cursor's turn hooks send the agent's last message, which can quote a
        // value it has just read from ~/.secrets (docs/session-secrets.md).
        var world = new NotifyWorld(AgentKind.Codex);

        await world.Controller().Notify("s1",
            new InternalController.NotifyBody("Deployed with token tok-0123456789abcdef.", "question"), CancellationToken.None);

        var (_, _, message) = Assert.Single(world.Notifier.Events);
        Assert.Equal("Deployed with token [secret:DEPLOY_TOKEN].", message);
        Assert.Equal(["s1"], world.Sessions.Masked);
    }

    [Fact]
    public async Task Notify_WrongToken_IsUnauthorizedAndSilent()
    {
        var world = new NotifyWorld(AgentKind.Codex);

        var result = await world.Controller(token: "wrong").Notify("s1",
            new InternalController.NotifyBody("hi", "question"), CancellationToken.None);

        Assert.IsType<UnauthorizedResult>(result);
        Assert.Empty(world.Notifier.Events);
        Assert.Empty(world.Store.QuestionPending);
    }

    [Theory]
    [InlineData(AgentKind.Codex, "Codex")]
    [InlineData(AgentKind.Cursor, "Cursor")]
    [InlineData(AgentKind.OpenClaw, "OpenClaw")]
    [InlineData(AgentKind.OpenCode, "OpenCode")]
    [InlineData(AgentKind.Claude, "Claude")]
    public void StatusText_NamesTheSessionsAgent(AgentKind agent, string expected)
    {
        var text = ChatFormatting.StatusText("Running", questionPending: false, pendingTool: null, link: null, agent);

        Assert.Contains($"⏳ {expected} is working.", text);
        if (agent != AgentKind.Claude) Assert.DoesNotContain("Claude", text);
    }

    [Fact]
    public void StatusText_WithoutAgent_StaysNeutral()
    {
        // The session may be gone by the time "!status" is asked — no provider gets named then.
        var text = ChatFormatting.StatusText("Running", false, null, null);
        Assert.Contains("⏳ The agent is working.", text);
    }

    [Fact]
    public void WorkingIndicatorFrames_NameTheAgent()
    {
        var frames = WorkingIndicator.FramesFor(AgentKind.Codex);
        Assert.Equal(4, frames.Count);
        Assert.All(frames, f => Assert.Contains("Codex is working", f));
        Assert.All(WorkingIndicator.Frames, f => Assert.Contains("The agent is working", f));
    }

    private sealed class NotifyWorld
    {
        public NotifyWorld(AgentKind agent)
        {
            Store = new RecordingStore(new SessionRecord
            {
                Id = "s1", Owner = "alice", Title = "Interactive task", Agent = agent,
                Mode = SessionMode.Interactive, AgentSessionId = "agent-s1", CallbackToken = "token-s1"
            });
        }

        public RecordingStore Store { get; }
        public RecordingNotifier Notifier { get; } = new();
        public MaskingSessions Sessions { get; } = new();

        public InternalController Controller(string? token = null)
        {
            var controller = new InternalController(Store, [Notifier], Sessions, null!, [], [], null!, null!)
            {
                ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() }
            };
            controller.Request.Headers["X-Agent-Token"] = token ?? "token-s1";
            return controller;
        }
    }

    /// <summary>Masks with one secret the session holds; the real lookup reads the session's copies.</summary>
    private sealed class MaskingSessions : AgentHub.Api.Services.ISessionService
    {
        public List<string> Masked { get; } = [];

        public Task<string> MaskSessionSecretsAsync(string sessionId, string text, CancellationToken ct = default)
        {
            Masked.Add(sessionId);
            return Task.FromResult(AgentHub.Api.Services.SecretMask.Apply(text,
                [new("DEPLOY_TOKEN", System.Text.Encoding.UTF8.GetBytes("tok-0123456789abcdef"))]));
        }

        public Task StoreCredentialsAsync(string owner, UserCredentials creds, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<CredentialStatus> GetCredentialStatusAsync(string owner, CancellationToken ct = default) => throw new NotSupportedException();
        public Task StoreProviderCredentialsAsync(string owner, AgentKind agent, string json, CancellationToken ct = default) => throw new NotSupportedException();
        public Task DeleteProviderCredentialsAsync(string owner, AgentKind agent, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<SessionInfo> CreateSessionAsync(string owner, CreateSessionRequest req, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<SessionInfo> DuplicateSessionAsync(string owner, string id, DuplicateSessionRequest request, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<SessionInfo> ResumeSessionAsync(string owner, string id, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<SessionInfo> PauseSessionAsync(string owner, string id, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<SessionInfo> UpdateSessionAsync(string owner, string id, UpdateSessionRequest req, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<SessionInfo>> ListSessionsAsync(string owner, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<SessionInfo?> GetSessionAsync(string owner, string id, CancellationToken ct = default) => throw new NotSupportedException();
        public Task ClearQuestionAsync(string owner, string id, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<string?> GetTranscriptAsync(string owner, string id, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<string?> MintArtifactUploadUrlAsync(string sessionId, string token, string name, CancellationToken ct = default) => throw new NotSupportedException();
        public Task DeleteSessionAsync(string owner, string id, CancellationToken ct = default) => throw new NotSupportedException();
    }

    private sealed class RecordingNotifier : INotifier
    {
        public List<(SessionRecord Session, string Event, string Message)> Events { get; } = [];

        public Task NotifyAsync(SessionRecord s, string eventType, string message, CancellationToken ct = default)
        {
            Events.Add((s, eventType, message));
            return Task.CompletedTask;
        }
    }

    private sealed class RecordingStore(SessionRecord record) : ISessionStore
    {
        public List<(string Id, bool Pending)> QuestionPending { get; } = [];

        public Task InitializeAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task UpsertAsync(SessionRecord r, CancellationToken ct = default) => Task.CompletedTask;
        public Task<SessionRecord?> GetAsync(string owner, string id, CancellationToken ct = default) =>
            Task.FromResult(record.Owner == owner && record.Id == id ? record : null);
        public Task<SessionRecord?> GetByIdAsync(string id, CancellationToken ct = default) =>
            Task.FromResult(record.Id == id ? record : null);
        public Task<SessionRecord?> GetByCallbackTokenAsync(string token, CancellationToken ct = default) =>
            Task.FromResult(record.CallbackToken == token ? record : null);
        public Task<IReadOnlyList<SessionRecord>> ListAsync(string owner, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<SessionRecord>>([record]);
        public Task UpdateStatusAsync(string id, string status, CancellationToken ct = default) => Task.CompletedTask;
        public Task SetQuestionPendingAsync(string id, bool pending, CancellationToken ct = default)
        {
            QuestionPending.Add((id, pending));
            return Task.CompletedTask;
        }
        public Task SetScrollbackAsync(string id, string text, CancellationToken ct = default) => Task.CompletedTask;
        public Task<string?> GetScrollbackAsync(string id, CancellationToken ct = default) => Task.FromResult<string?>(null);
        public Task DeleteAsync(string id, CancellationToken ct = default) => Task.CompletedTask;
    }
}
