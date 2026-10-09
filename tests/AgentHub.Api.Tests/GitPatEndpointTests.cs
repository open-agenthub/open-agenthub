using System.Security.Claims;
using AgentHub.Api.Controllers;
using AgentHub.Api.Models;
using AgentHub.Api.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Xunit;

namespace AgentHub.Api.Tests;

/// <summary>
/// The git-pats endpoints are the only way to add or rotate a PAT; the merge-style PUT cannot
/// express a list. The owner comes from the token, never from the request.
/// </summary>
public class GitPatEndpointTests
{
    [Fact]
    public async Task Upsert_StoresForTheCallerAndAnswersWithoutTheToken()
    {
        var service = new RecordingSessionService();
        var controller = Controller(service, "alice");

        var result = await controller.UpsertGitPat(
            new UpsertGitPatRequest { Kind = "gitlab", Host = "gitlab.example.com", Token = "glpat-secret" },
            CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var info = Assert.IsType<GitPatInfo>(ok.Value);
        Assert.Equal(("gitlab", "gitlab.example.com"), (info.Kind, info.Host));
        Assert.Equal([("alice", "gitlab.example.com")], service.Upserts);
    }

    [Fact]
    public async Task Upsert_ReportsAValidationFailureAs400WithTheReason()
    {
        var service = new RecordingSessionService { Reject = "The token kind must be 'gitlab' or 'github'." };
        var controller = Controller(service, "alice");

        var result = await controller.UpsertGitPat(
            new UpsertGitPatRequest { Kind = "bitbucket", Host = "git.example.com", Token = "x" },
            CancellationToken.None);

        var bad = Assert.IsType<BadRequestObjectResult>(result.Result);
        Assert.Contains("gitlab", bad.Value?.ToString());
    }

    [Fact]
    public async Task Delete_IsScopedToTheCallerAndAlwaysNoContent()
    {
        var service = new RecordingSessionService();

        var first = await Controller(service, "alice").DeleteGitPat("id-1", CancellationToken.None);
        var second = await Controller(service, "bob").DeleteGitPat("id-1", CancellationToken.None);

        Assert.IsType<NoContentResult>(first);
        Assert.IsType<NoContentResult>(second);
        Assert.Equal([("alice", "id-1"), ("bob", "id-1")], service.Deletes);
    }

    private static CredentialsController Controller(ISessionService service, string owner)
    {
        var user = new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim("preferred_username", owner)], "test"));
        return new CredentialsController(service)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext { User = user }
            }
        };
    }

    private sealed class RecordingSessionService : ISessionService
    {
        public List<(string Owner, string Host)> Upserts { get; } = [];
        public List<(string Owner, string Id)> Deletes { get; } = [];
        public string? Reject { get; init; }

        public Task<GitPatInfo> UpsertGitPatAsync(string owner, UpsertGitPatRequest request, CancellationToken ct = default)
        {
            if (Reject is not null) throw new ArgumentException(Reject);
            Upserts.Add((owner, request.Host!));
            return Task.FromResult(new GitPatInfo("new-id", request.Kind!, request.Host!));
        }

        public Task DeleteGitPatAsync(string owner, string id, CancellationToken ct = default)
        {
            Deletes.Add((owner, id));
            return Task.CompletedTask;
        }

        public Task StoreCredentialsAsync(string owner, UserCredentials creds, CancellationToken ct = default) =>
            Task.CompletedTask;
        public Task<CredentialStatus> GetCredentialStatusAsync(string owner, CancellationToken ct = default) =>
            Task.FromResult(new CredentialStatus());
        public Task StoreProviderCredentialsAsync(string owner, AgentKind agent, string json, CancellationToken ct = default) =>
            throw new NotSupportedException();
        public Task DeleteProviderCredentialsAsync(string owner, AgentKind agent, CancellationToken ct = default) =>
            throw new NotSupportedException();
        public Task<SessionInfo> CreateSessionAsync(string owner, CreateSessionRequest req, CancellationToken ct = default) =>
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
        public Task<SessionInfo?> GetSessionAsync(string owner, string id, CancellationToken ct = default) =>
            throw new NotSupportedException();
        public Task ClearQuestionAsync(string owner, string id, CancellationToken ct = default) =>
            throw new NotSupportedException();
        public Task<string?> GetTranscriptAsync(string owner, string id, CancellationToken ct = default) =>
            throw new NotSupportedException();
        public Task DeleteSessionAsync(string owner, string id, CancellationToken ct = default) =>
            throw new NotSupportedException();
        public Task<string?> MintArtifactUploadUrlAsync(string sessionId, string token, string name, CancellationToken ct = default) =>
            throw new NotSupportedException();
    }
}
