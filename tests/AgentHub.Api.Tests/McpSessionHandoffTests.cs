using System.Security.Claims;
using AgentHub.Api.Mcp;
using AgentHub.Api.Models;
using AgentHub.Api.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using ModelContextProtocol;
using Xunit;

namespace AgentHub.Api.Tests;

/// <summary>
/// The MCP half of "an API creates a session, a person takes it over": the create tool has to pass
/// the caller's prompts on and return the link, and the transcript tool has to page.
/// </summary>
public class McpSessionHandoffTests
{
    [Fact]
    public async Task SessionCreate_PassesPromptAndSystemPromptAndReturnsTheUrl()
    {
        var svc = new FakeSessions();
        var tools = Tools(svc);

        var created = await tools.CreateSession(
            title: "Build triage", prompt: "triage the failing build",
            systemPrompt: "You review, you do not commit.", mode: "Interactive");

        Assert.Equal("triage the failing build", svc.LastRequest!.Prompt);
        Assert.Equal("You review, you do not commit.", svc.LastRequest.SystemPrompt);
        Assert.Equal(SessionMode.Interactive, svc.LastRequest.Mode);
        // The url is what a caller hands to the person who takes the session on.
        Assert.Equal("https://agenthub.example.com/s/sess-1", created.Url);
    }

    [Fact]
    public async Task SessionTranscript_PagesFromTheCursorAndReportsWhenToStop()
    {
        var svc = new FakeSessions
        {
            Session = Info("Running"),
            Transcript = "first chunk|second chunk"
        };
        var tools = Tools(svc);

        var first = await tools.GetSessionTranscript("sess-1", offset: null, maxChars: 11);
        Assert.Equal("first chunk", first.Text);
        Assert.True(first.Truncated);
        Assert.True(first.Running);

        var second = await tools.GetSessionTranscript("sess-1", first.NextOffset, maxChars: null);
        Assert.Equal("|second chunk", second.Text);
        Assert.False(second.Truncated);

        svc.Session = Info("Succeeded");
        var last = await tools.GetSessionTranscript("sess-1", second.NextOffset, maxChars: null);
        Assert.Equal("", last.Text);
        Assert.False(last.Running);
    }

    [Fact]
    public async Task SessionTranscript_ForAnUnknownSession_FailsWithoutReadingAnything()
    {
        var svc = new FakeSessions { Session = null, Transcript = "leak me" };
        var tools = Tools(svc);

        var error = await Assert.ThrowsAsync<McpException>(
            () => tools.GetSessionTranscript("sess-1", null, null));

        Assert.Equal("session_not_found", error.Message);
        Assert.Equal(0, svc.TranscriptCalls);
    }

    // ------------------------------------------------------------------ fixtures

    private static SessionInfo Info(string phase) => new()
    {
        Id = "sess-1", Title = "Build triage", Owner = "alice",
        Mode = SessionMode.Interactive, Phase = phase,
        Url = "https://agenthub.example.com/s/sess-1"
    };

    private static AgentHubMcpTools Tools(FakeSessions svc)
    {
        var context = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity([new Claim("preferred_username", "alice")], "test"))
        };
        return new AgentHubMcpTools(
            new HttpContextAccessor { HttpContext = context }, svc,
            NullLogger<AgentHubMcpTools>.Instance);
    }

    private sealed class FakeSessions : ISessionService
    {
        public CreateSessionRequest? LastRequest { get; private set; }
        public SessionInfo? Session { get; set; }
        public string? Transcript { get; init; }
        public int TranscriptCalls { get; private set; }

        public Task<SessionInfo> CreateSessionAsync(string owner, CreateSessionRequest req, CancellationToken ct = default)
        {
            LastRequest = req;
            return Task.FromResult(Info("Pending"));
        }

        public Task<SessionInfo?> GetSessionAsync(string owner, string id, CancellationToken ct = default) =>
            Task.FromResult(Session);

        /// <summary>Mirrors production: no visible session means no transcript, whatever is stored.</summary>
        public Task<string?> GetTranscriptAsync(string owner, string id, CancellationToken ct = default)
        {
            TranscriptCalls++;
            return Task.FromResult(Session is null ? null : Transcript);
        }

        public Task StoreCredentialsAsync(string owner, UserCredentials creds, CancellationToken ct = default) =>
            throw new NotSupportedException();
        public Task<CredentialStatus> GetCredentialStatusAsync(string owner, CancellationToken ct = default) =>
            throw new NotSupportedException();
        public Task DeleteProviderCredentialsAsync(string owner, AgentKind agent, CancellationToken ct = default) =>
            throw new NotSupportedException();
        public Task StoreProviderCredentialsAsync(string owner, AgentKind agent, string json, CancellationToken ct = default) =>
            throw new NotSupportedException();
        public Task<SessionInfo> DuplicateSessionAsync(string owner, string id, DuplicateSessionRequest request, CancellationToken ct = default) =>
            throw new NotSupportedException();
        public Task<SessionInfo> ResumeSessionAsync(string owner, string id, CancellationToken ct = default) =>
            throw new NotSupportedException();
        public Task<SessionInfo> PauseSessionAsync(string owner, string id, CancellationToken ct = default) =>
            throw new NotSupportedException();
        public Task<SessionInfo> UpdateSessionAsync(string owner, string id, UpdateSessionRequest req, CancellationToken ct = default) =>
            throw new NotSupportedException();
        public Task<IReadOnlyList<SessionInfo>> ListSessionsAsync(string owner, CancellationToken ct = default) =>
            throw new NotSupportedException();
        public Task ClearQuestionAsync(string owner, string id, CancellationToken ct = default) =>
            throw new NotSupportedException();
        public Task<string?> MintArtifactUploadUrlAsync(string sessionId, string token, string name, CancellationToken ct = default) =>
            throw new NotSupportedException();
        public Task DeleteSessionAsync(string owner, string id, CancellationToken ct = default) =>
            throw new NotSupportedException();
    }
}
