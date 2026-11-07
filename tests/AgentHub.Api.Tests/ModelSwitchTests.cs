using System.Net;
using System.Reflection;
using System.Security.Claims;
using System.Text.Json;
using AgentHub.Api.Controllers;
using AgentHub.Api.Ee.Sharing;
using AgentHub.Api.Models;
using AgentHub.Api.Persistence;
using AgentHub.Api.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace AgentHub.Api.Tests;

/// <summary>Switching a running session's model from the Workspace (#478, docs/design/live-model-switch.md).</summary>
public class ModelSwitchTests
{
    [Theory]
    [InlineData(AgentKind.Claude, AgentAuthMode.Subscription, SessionUiMode.Terminal, "live")]
    [InlineData(AgentKind.Claude, AgentAuthMode.Subscription, SessionUiMode.Chat, "restart")]
    [InlineData(AgentKind.Codex, AgentAuthMode.Subscription, SessionUiMode.Terminal, "live")]
    [InlineData(AgentKind.Codex, AgentAuthMode.CustomEndpoint, SessionUiMode.Terminal, "restart")]
    [InlineData(AgentKind.OpenCode, AgentAuthMode.ApiKey, SessionUiMode.Terminal, "live")]
    [InlineData(AgentKind.OpenClaw, AgentAuthMode.ApiKey, SessionUiMode.Terminal, "live")]
    [InlineData(AgentKind.Cursor, AgentAuthMode.Subscription, SessionUiMode.Terminal, "restart")]
    public void EachAgentSwitchesTheWayItsPinnedCliWasFoundTo(AgentKind agent, AgentAuthMode auth, string ui, string mode)
    {
        var session = Record(agent, auth);
        session.UiMode = ui;
        Assert.Equal(mode, ModelSwitchPolicy.ModeFor(session));
        session.Mode = SessionMode.Autonomous;
        Assert.Equal("unsupported", ModelSwitchPolicy.ModeFor(session));
    }

    [Fact]
    public async Task ALiveSwitchIsStoredOnlyAfterTheCliConfirmedIt()
    {
        var pod = new Pod(HttpStatusCode.OK, """{"model":"gpt-6-luna","confirmation":"• Model changed to gpt-6-luna medium"}""");
        var store = new Store();
        var session = Record(AgentKind.Codex, AgentAuthMode.Subscription);

        var outcome = await Switcher(pod, store, running: true).SwitchAsync(session, " gpt-6-luna ", default);

        Assert.Equal(new ModelSwitchOutcome { Model = "gpt-6-luna", Mode = "live", Confirmation = "• Model changed to gpt-6-luna medium" }, outcome);
        var request = Assert.Single(pod.Requests);
        Assert.Equal("http://127.0.0.1:7681/agenthub/model", request.Uri);
        Assert.Equal("secret", request.Token);
        Assert.Equal("""{"model":"gpt-6-luna"}""", request.Body);
        Assert.Equal([(session.Id, "gpt-6-luna")], store.Writes);
    }

    [Theory]
    [InlineData(HttpStatusCode.Conflict, """{"error":"turn_running"}""", 409, "turn_running")]
    [InlineData(HttpStatusCode.Conflict, """{"error":"question_open"}""", 409, "question_open")]
    [InlineData((HttpStatusCode)422, """{"error":"model_refused","detail":"Model 'x' is unavailable"}""", 422, "model_refused")]
    [InlineData(HttpStatusCode.GatewayTimeout, """{"error":"model_switch_not_confirmed"}""", 504, "model_switch_not_confirmed")]
    [InlineData(HttpStatusCode.NotFound, """{"error":"not_found"}""", 409, "runtime_too_old")]
    [InlineData(HttpStatusCode.InternalServerError, """{"error":"<script>"}""", 502, "model_switch_failed")]
    public async Task APodRefusalKeepsTheStoredModelAndSaysWhy(HttpStatusCode podStatus, string body, int status, string code)
    {
        var store = new Store();
        var refused = await Assert.ThrowsAsync<ModelSwitchRefusedException>(() =>
            Switcher(new Pod(podStatus, body), store, running: true).SwitchAsync(Record(AgentKind.Codex, AgentAuthMode.Subscription), "x", default));
        Assert.Equal((status, code), (refused.Status, refused.Code));
        Assert.Empty(store.Writes);
    }

    [Fact]
    public async Task RestartAgentsAndStoppedSessionsStoreTheModelWithoutAskingThePod()
    {
        var pod = new Pod(HttpStatusCode.OK, "{}");
        var store = new Store();
        var cursor = Record(AgentKind.Cursor, AgentAuthMode.Subscription);
        var stoppedClaude = Record(AgentKind.Claude, AgentAuthMode.Subscription);

        Assert.Equal("restart", (await Switcher(pod, store, running: true).SwitchAsync(cursor, "gpt-6", default)).Mode);
        Assert.Equal("restart", (await Switcher(pod, store, running: false).SwitchAsync(stoppedClaude, "claude-sonnet-5-5", default)).Mode);

        Assert.Empty(pod.Requests);
        Assert.Equal([(cursor.Id, "gpt-6"), (stoppedClaude.Id, "claude-sonnet-5-5")], store.Writes);
    }

    [Theory]
    [InlineData(AgentKind.OpenCode)]
    [InlineData(AgentKind.OpenClaw)]
    public async Task AgentsWhoseRestartWouldNotSwitchRefuseWhenStopped(AgentKind agent)
    {
        var store = new Store();
        var refused = await Assert.ThrowsAsync<ModelSwitchRefusedException>(() =>
            Switcher(new Pod(HttpStatusCode.OK, "{}"), store, running: false).SwitchAsync(Record(agent, AgentAuthMode.ApiKey), "a/b", default));
        Assert.Equal((409, "session_not_running"), (refused.Status, refused.Code));
        Assert.Empty(store.Writes);
    }

    [Fact]
    public async Task OpenClawSwitchesLiveButStoresNothingItsStartWouldIgnore()
    {
        var store = new Store();
        var outcome = await Switcher(new Pod(HttpStatusCode.OK, """{"confirmation":"model set to mock/mock-b"}"""), store, running: true)
            .SwitchAsync(Record(AgentKind.OpenClaw, AgentAuthMode.ApiKey), "mock/mock-b", default);
        Assert.Equal("live", outcome.Mode);
        Assert.Empty(store.Writes);
    }

    [Theory]
    [InlineData("two words")]
    [InlineData("-c")]
    [InlineData("")]
    [InlineData("a\u0007b")]
    public async Task UnsafeIdsNeverReachThePod(string model)
    {
        var pod = new Pod(HttpStatusCode.OK, "{}");
        var refused = await Assert.ThrowsAsync<ModelSwitchRefusedException>(() =>
            Switcher(pod, new Store(), running: true).SwitchAsync(Record(AgentKind.Claude, AgentAuthMode.Subscription), model, default));
        Assert.Equal("invalid_model", refused.Code);
        Assert.Empty(pod.Requests);
    }

    [Theory]
    [InlineData(SessionAccessLevel.Owner, 200)]
    [InlineData(SessionAccessLevel.Collaborator, 200)]
    [InlineData(SessionAccessLevel.Viewer, 403)]
    public async Task OwnersAndCollaboratorsSwitchViewersOnlyRead(SessionAccessLevel level, int status)
    {
        var session = Record(AgentKind.Codex, AgentAuthMode.Subscription);
        var switcher = new RecordingSwitch();
        var controller = new SessionModelsController(new Access { Resolved = new(session, level, null) }, SessionList(session),
            new NoProbes(), new CatalogStore(), switcher) { ControllerContext = UserContext("someone") };

        var result = await controller.Switch(session.Id, new ModelSwitchRequest("gpt-6-luna"), default);

        Assert.Equal(status, result is OkObjectResult ? 200 : Assert.IsType<ObjectResult>(result).StatusCode);
        Assert.Equal(status == 200 ? 1 : 0, switcher.Calls);
        // The catalog read tells the Workspace how a pick would apply and whether this reader may make one.
        var view = Assert.IsType<ModelCatalogView>(Assert.IsType<OkObjectResult>(await controller.Read(session.Id, false, default)).Value);
        Assert.Equal(("live", level >= SessionAccessLevel.Collaborator), (view.SwitchMode, view.CanSwitch));
    }

    [Fact]
    public async Task ARefusalReachesTheWorkspaceAsItsCodeAndALineOfDetail()
    {
        var session = Record(AgentKind.Codex, AgentAuthMode.Subscription);
        var controller = new SessionModelsController(new Access { Resolved = new(session, SessionAccessLevel.Owner, null) }, SessionList(session),
            new NoProbes(), new CatalogStore(), new RecordingSwitch { Refusal = new(422, "model_refused", "Model 'x' is unavailable") })
        { ControllerContext = UserContext("owner") };

        var result = Assert.IsType<ObjectResult>(await controller.Switch(session.Id, new ModelSwitchRequest("x"), default));

        Assert.Equal(422, result.StatusCode);
        var body = JsonDocument.Parse(JsonSerializer.Serialize(result.Value)).RootElement;
        Assert.Equal(("model_refused", "Model 'x' is unavailable"), (body.GetProperty("error").GetString(), body.GetProperty("detail").GetString()));
    }

    [PostgreSqlFact]
    public async Task ThePostgresStoreRecordsTheSwitchedModelOnly()
    {
        await using var database = await PostgresSharingDatabase.CreateAsync(pooling: false);
        var session = Record(AgentKind.Codex, AgentAuthMode.Subscription);
        session.ModelName = "gpt-6.1-sol";
        session.SystemPrompt = "kept";
        await database.UpsertSessionAsync(session);

        await database.Sessions.SetModelNameAsync(session.Id, "gpt-6-luna");

        var read = await database.Sessions.GetByIdAsync(session.Id);
        Assert.Equal(("gpt-6-luna", "kept"), (read!.ModelName, read.SystemPrompt));
    }

    // ---------- fakes ----------

    private static SessionRecord Record(AgentKind agent, AgentAuthMode auth) => new()
    {
        Id = "s-" + Guid.NewGuid().ToString("N")[..8], Owner = "owner", Title = "Session", Mode = SessionMode.Interactive,
        Agent = agent, AuthMode = auth, CallbackToken = "secret",
        ModelEndpointUrl = auth == AgentAuthMode.CustomEndpoint ? "https://llm.example.com/v1" : null,
        ModelName = auth == AgentAuthMode.CustomEndpoint ? "served-a" : null
    };

    private static SessionModelSwitch Switcher(Pod pod, Store store, bool running) =>
        new(TestPods.Live(new HttpClient(pod)), Sessions(running), store, new ConfigurationBuilder().Build());

    private static ControllerContext UserContext(string user) => new()
    {
        HttpContext = new DefaultHttpContext { User = new ClaimsPrincipal(new ClaimsIdentity([new Claim("preferred_username", user)], "test")) }
    };

    private sealed class Pod(HttpStatusCode status, string body) : HttpMessageHandler
    {
        public readonly List<(string Uri, string Token, string Body)> Requests = [];
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Requests.Add((request.RequestUri!.ToString(), request.Headers.GetValues("X-Agent-Token").Single(),
                request.Content is null ? "" : await request.Content.ReadAsStringAsync(ct)));
            return new HttpResponseMessage(status) { Content = new StringContent(body) };
        }
    }

    private sealed class Store : ISessionStore
    {
        public readonly List<(string, string?)> Writes = [];
        public Task SetModelNameAsync(string id, string? model, CancellationToken ct = default) { Writes.Add((id, model)); return Task.CompletedTask; }
        public Task InitializeAsync(CancellationToken ct = default) => throw new NotSupportedException();
        public Task UpsertAsync(SessionRecord r, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<SessionRecord?> GetAsync(string owner, string id, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<SessionRecord?> GetByCallbackTokenAsync(string token, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<SessionRecord>> ListAsync(string owner, CancellationToken ct = default) => throw new NotSupportedException();
        public Task UpdateStatusAsync(string id, string status, CancellationToken ct = default) => throw new NotSupportedException();
        public Task SetQuestionPendingAsync(string id, bool pending, CancellationToken ct = default) => throw new NotSupportedException();
        public Task SetScrollbackAsync(string id, string text, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<string?> GetScrollbackAsync(string id, CancellationToken ct = default) => throw new NotSupportedException();
        public Task DeleteAsync(string id, CancellationToken ct = default) => throw new NotSupportedException();
    }

    private sealed class RecordingSwitch : ISessionModelSwitch
    {
        public int Calls;
        public ModelSwitchRefusedException? Refusal;
        public Task<ModelSwitchOutcome> SwitchAsync(SessionRecord session, string? model, CancellationToken ct)
        {
            Calls++;
            return Refusal is not null ? throw Refusal : Task.FromResult(new ModelSwitchOutcome { Model = model!, Mode = "live" });
        }
    }

    private sealed class NoProbes : ISessionModelCatalogs
    {
        public Task RecordAsync(SessionRecord session, ModelCatalog catalog, CancellationToken ct) => Task.CompletedTask;
        public Task<ModelCatalog?> ProbeAsync(SessionRecord session, bool refresh, CancellationToken ct) => Task.FromResult<ModelCatalog?>(null);
    }

    private sealed class CatalogStore : IModelCatalogStore
    {
        public Task<StoredModelCatalog?> GetAsync(ModelCatalogKey key, CancellationToken ct = default) =>
            Task.FromResult<StoredModelCatalog?>(new(new ModelCatalog { State = "ok", Models = [new CatalogModel { Id = "gpt-6-luna" }] }, DateTimeOffset.UtcNow));
        public Task PutAsync(ModelCatalogKey key, ModelCatalog catalog, DateTimeOffset fetchedAt, CancellationToken ct = default) => Task.CompletedTask;
    }

    private sealed class Access : ISessionAccessService
    {
        public SessionAccessResult? Resolved;
        public Task<SessionAccessResult?> ResolveUserAsync(string principal, string id, CancellationToken ct = default) => Task.FromResult(Resolved);
        public Task<SessionAccessResult?> ResolveTokenAsync(string token, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<SessionAccessResult?> ResolveTokenReadOnlyAsync(string token, CancellationToken ct = default) => throw new NotSupportedException();
    }

    public class LiveProxy : DispatchProxy
    {
        public bool Running;
        protected override object? Invoke(MethodInfo? method, object?[]? args) =>
            method!.Name == nameof(ISessionService.GetSessionAsync)
                ? Task.FromResult<SessionInfo?>(new SessionInfo
                {
                    Id = (string)args![1]!, Owner = "owner", Title = "S", Mode = SessionMode.Interactive,
                    Phase = Running ? "Running" : "Paused", PodIp = Running ? "127.0.0.1" : null
                })
                : throw new NotSupportedException(method.Name);
    }

    private static ISessionService Sessions(bool running)
    {
        var proxy = DispatchProxy.Create<ISessionService, LiveProxy>();
        ((LiveProxy)proxy).Running = running;
        return proxy;
    }

    public class ListProxy : DispatchProxy
    {
        public IReadOnlyList<SessionRecord> Records = [];
        protected override object? Invoke(MethodInfo? method, object?[]? args) =>
            method!.Name == nameof(ISessionStore.GetByIdAsync)
                ? Task.FromResult(Records.FirstOrDefault(r => r.Id == (string)args![0]!))
                : throw new NotSupportedException(method.Name);
    }

    private static ISessionStore SessionList(params SessionRecord[] records)
    {
        var proxy = DispatchProxy.Create<ISessionStore, ListProxy>();
        ((ListProxy)proxy).Records = records;
        return proxy;
    }
}
