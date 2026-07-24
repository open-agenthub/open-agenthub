using System.Security.Claims;
using System.Text.Json;
using AgentHub.Api.Controllers;
using AgentHub.Api.Models;
using AgentHub.Api.Permissions;
using AgentHub.Api.Persistence;
using AgentHub.Api.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Xunit;

namespace AgentHub.Api.Tests;

/// <summary>
/// The permission request lifecycle across both controllers: the agent hook side
/// (InternalController — requests stay pending without a messenger, always-allow
/// rules short-circuit) and the in-app side (SessionsController — list + decide).
/// </summary>
public class PermissionFlowControllerTests
{
    [PostgreSqlFact]
    public async Task RequestPermission_StaysPendingWithoutMessengerTarget()
    {
        await using var database = await PostgresPermissionDatabase.CreateAsync();
        var controller = Internal(database.Store);

        var result = await controller.RequestPermission("session-1", new InternalController.PermissionBody("Bash", null), CancellationToken.None);

        // No notifier posted, yet the request is held for the in-app approval UI.
        var id = Json(result).GetProperty("id").GetString();
        Assert.False(string.IsNullOrEmpty(id));
        var pending = await database.Store.GetPendingRequestsAsync("session-1");
        Assert.Equal(id, Assert.Single(pending).Id);
    }

    [PostgreSqlFact]
    public async Task RequestPermission_AllowAlwaysRuleShortCircuits()
    {
        await using var database = await PostgresPermissionDatabase.CreateAsync();
        await database.Store.AddAlwaysAllowRuleAsync("session-1", "Bash");
        var controller = Internal(database.Store);

        var result = await controller.RequestPermission("session-1", new InternalController.PermissionBody("Bash", null), CancellationToken.None);

        Assert.Equal("allow", Json(result).GetProperty("decision").GetString());
        Assert.Empty(await database.Store.GetPendingRequestsAsync("session-1"));
    }

    [PostgreSqlFact]
    public async Task DecidePermission_ResolvesPendingRequest()
    {
        await using var database = await PostgresPermissionDatabase.CreateAsync();
        var internalController = Internal(database.Store);
        var created = await internalController.RequestPermission("session-1", new InternalController.PermissionBody("Bash", null), CancellationToken.None);
        var reqId = Json(created).GetProperty("id").GetString()!;
        var controller = Sessions(database.Store);

        var decided = await controller.DecidePermission("session-1", reqId,
            new SessionsController.PermissionDecisionBody("allow"), CancellationToken.None);

        Assert.Equal("allow", Json(decided).GetProperty("decision").GetString());
        Assert.Equal("allow", await database.Store.GetDecisionAsync(reqId));

        // Deciding again reports the final state instead of flipping it.
        var again = await controller.DecidePermission("session-1", reqId,
            new SessionsController.PermissionDecisionBody("deny"), CancellationToken.None);
        var conflict = Assert.IsType<ConflictObjectResult>(again);
        using var doc = JsonDocument.Parse(JsonSerializer.Serialize(conflict.Value));
        Assert.Equal("allow", doc.RootElement.GetProperty("decision").GetString());
    }

    [PostgreSqlFact]
    public async Task DecidePermission_AllowAlwaysSilencesTheNextRequest()
    {
        await using var database = await PostgresPermissionDatabase.CreateAsync();
        var internalController = Internal(database.Store);
        var created = await internalController.RequestPermission("session-1", new InternalController.PermissionBody("Bash", null), CancellationToken.None);
        var reqId = Json(created).GetProperty("id").GetString()!;

        await Sessions(database.Store).DecidePermission("session-1", reqId,
            new SessionsController.PermissionDecisionBody("allowAlways"), CancellationToken.None);

        var next = await internalController.RequestPermission("session-1", new InternalController.PermissionBody("Bash", null), CancellationToken.None);
        Assert.Equal("allow", Json(next).GetProperty("decision").GetString());
    }

    [PostgreSqlFact]
    public async Task DecidePermission_ValidatesInputAndOwnership()
    {
        await using var database = await PostgresPermissionDatabase.CreateAsync();
        var controller = Sessions(database.Store);

        Assert.IsType<BadRequestObjectResult>(await controller.DecidePermission("session-1", "whatever",
            new SessionsController.PermissionDecisionBody("expired"), CancellationToken.None));
        Assert.IsType<NotFoundResult>(await controller.DecidePermission("session-1", "unknown-id",
            new SessionsController.PermissionDecisionBody("allow"), CancellationToken.None));
        Assert.IsType<NotFoundResult>(await controller.DecidePermission("not-mine", "whatever",
            new SessionsController.PermissionDecisionBody("allow"), CancellationToken.None));
    }

    [PostgreSqlFact]
    public async Task PendingPermissions_ListsOnlyTheOwnedSession()
    {
        await using var database = await PostgresPermissionDatabase.CreateAsync();
        var internalController = Internal(database.Store);
        await internalController.RequestPermission("session-1", new InternalController.PermissionBody("Bash", null), CancellationToken.None);
        var controller = Sessions(database.Store);

        var list = Json(await controller.PendingPermissions("session-1", CancellationToken.None));
        Assert.Equal(1, list.GetArrayLength());
        Assert.Equal("Bash", list[0].GetProperty("tool").GetString());

        Assert.IsType<NotFoundResult>(await controller.PendingPermissions("not-mine", CancellationToken.None));
    }

    // ------------------------------------------------------------------ fixtures

    private static JsonElement Json(IActionResult result)
    {
        var ok = Assert.IsType<OkObjectResult>(result);
        return JsonDocument.Parse(JsonSerializer.Serialize(ok.Value)).RootElement;
    }

    private static InternalController Internal(PermissionStore store)
    {
        var session = new SessionRecord
        {
            Id = "session-1", Owner = "alice", CallbackToken = "callback-token",
            Mode = SessionMode.Interactive, Agent = AgentKind.Claude, AuthMode = AgentAuthMode.ApiKey
        };
        var controller = new InternalController(
            new SingleSessionStore(session), [], null!, store, [], [], null!, null!)
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() }
        };
        controller.Request.Headers["X-Agent-Token"] = "callback-token";
        return controller;
    }

    private static SessionsController Sessions(PermissionStore store)
    {
        var controller = new SessionsController(new OwnedSessionService("alice", "session-1"), store, [])
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() }
        };
        controller.HttpContext.User = new ClaimsPrincipal(
            new ClaimsIdentity([new Claim("preferred_username", "alice")], "test"));
        return controller;
    }

    private sealed class SingleSessionStore(SessionRecord session) : ISessionStore
    {
        public Task InitializeAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task UpsertAsync(SessionRecord record, CancellationToken ct = default) => Task.CompletedTask;
        public Task<SessionRecord?> GetAsync(string owner, string id, CancellationToken ct = default) => Task.FromResult<SessionRecord?>(null);
        public Task<SessionRecord?> GetByCallbackTokenAsync(string token, CancellationToken ct = default) =>
            Task.FromResult<SessionRecord?>(token == session.CallbackToken ? session : null);
        public Task<IReadOnlyList<SessionRecord>> ListAsync(string owner, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<SessionRecord>>([]);
        public Task UpdateStatusAsync(string id, string status, CancellationToken ct = default) => Task.CompletedTask;
        public Task SetQuestionPendingAsync(string id, bool pending, CancellationToken ct = default) => Task.CompletedTask;
        public Task SetScrollbackAsync(string id, string text, CancellationToken ct = default) => Task.CompletedTask;
        public Task<string?> GetScrollbackAsync(string id, CancellationToken ct = default) => Task.FromResult<string?>(null);
        public Task DeleteAsync(string id, CancellationToken ct = default) => Task.CompletedTask;
    }

    /// <summary>Grants "alice" exactly one session; everything else is unknown/foreign.</summary>
    private sealed class OwnedSessionService(string owner, string sessionId) : ISessionService
    {
        public Task<SessionInfo?> GetSessionAsync(string o, string id, CancellationToken ct = default) =>
            Task.FromResult<SessionInfo?>(o == owner && id == sessionId
                ? new SessionInfo { Id = sessionId, Title = "t", Owner = owner, Mode = SessionMode.Interactive, Phase = "Running" }
                : null);

        public Task StoreCredentialsAsync(string o, UserCredentials creds, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<CredentialStatus> GetCredentialStatusAsync(string o, CancellationToken ct = default) => throw new NotSupportedException();
        public Task StoreProviderCredentialsAsync(string o, AgentKind agent, string json, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<SessionInfo> CreateSessionAsync(string o, CreateSessionRequest req, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<SessionInfo> DuplicateSessionAsync(string o, string id, DuplicateSessionRequest request, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<SessionInfo> ResumeSessionAsync(string o, string id, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<SessionInfo> PauseSessionAsync(string o, string id, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<SessionInfo> UpdateSessionAsync(string o, string id, UpdateSessionRequest req, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<SessionInfo>> ListSessionsAsync(string o, CancellationToken ct = default) => throw new NotSupportedException();
        public Task ClearQuestionAsync(string o, string id, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<string?> GetTranscriptAsync(string o, string id, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<string?> MintArtifactUploadUrlAsync(string sessionId2, string token, string name, CancellationToken ct = default) => throw new NotSupportedException();
        public Task DeleteSessionAsync(string o, string id, CancellationToken ct = default) => throw new NotSupportedException();
    }
}
