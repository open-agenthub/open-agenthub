using System.Security.Claims;
using AgentHub.Api.Controllers;
using AgentHub.Api.Models;
using AgentHub.Api.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Xunit;

namespace AgentHub.Api.Tests;

/// <summary>A project selects which stored secrets its sessions get (docs/session-secrets.md).</summary>
public class ProjectSecretSelectionTests
{
    [Fact]
    public async Task AnUnknownSecretId_IsA400_AndNothingIsStored()
    {
        var store = new InMemoryProjectStore();
        var project = store.Add("alice");
        var sessions = new SecretSessions();

        var result = await Controller(store, sessions).Update(project.Id, new UpdateProjectRequest(null, null, null, ["nope"]), CancellationToken.None);

        Assert.Contains("nope", Assert.IsType<BadRequestObjectResult>(result.Result).Value!.ToString());
        Assert.Equal(0, sessions.Syncs);
    }

    [Fact]
    public async Task ChangingTheSelection_BringsRunningSessionsUpToDate()
    {
        var store = new InMemoryProjectStore();
        var project = store.Add("alice");
        var sessions = new SecretSessions();

        Assert.IsType<OkObjectResult>((await Controller(store, sessions)
            .Update(project.Id, new UpdateProjectRequest(null, null, null, ["s1"]), CancellationToken.None)).Result);
        Assert.Equal(1, sessions.Syncs);

        // A rename does not touch the selection and has nothing to bring up to date.
        await Controller(store, sessions).Update(project.Id, new UpdateProjectRequest("New name", null, null), CancellationToken.None);
        Assert.Equal(1, sessions.Syncs);
    }

    [Fact]
    public async Task DeletingAProject_BringsItsSessionsUpToDate()
    {
        // Its sessions are now outside any project, where following the project means nothing.
        var store = new InMemoryProjectStore();
        var project = store.Add("alice");
        var sessions = new SecretSessions();

        Assert.IsType<NoContentResult>(await Controller(store, sessions).Delete(project.Id, CancellationToken.None));
        Assert.Equal(1, sessions.Syncs);
    }

    private static ProjectsController Controller(InMemoryProjectStore store, ISessionService sessions) => new(store, sessions)
    {
        ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext
            {
                User = new ClaimsPrincipal(new ClaimsIdentity([new Claim("preferred_username", "alice")], "test"))
            }
        }
    };

    private sealed class SecretSessions : ISessionService
    {
        public int Syncs { get; private set; }

        public Task<IReadOnlyList<SessionSecretInfo>> ListSessionSecretsAsync(string owner, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<SessionSecretInfo>>([new("s1", "DEPLOY_TOKEN", "env", null, DateTime.UtcNow)]);

        public Task SyncSessionSecretsAsync(string owner, CancellationToken ct = default)
        {
            Syncs++;
            return Task.CompletedTask;
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
}
