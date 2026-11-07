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

/// <summary>Account-aware model discovery (#461, docs/design/model-discovery.md).</summary>
public class ModelDiscoveryTests
{
    // ---------- choosing a model ----------

    [Theory]
    [InlineData(AgentKind.Claude, AgentAuthMode.Subscription)]
    [InlineData(AgentKind.Claude, AgentAuthMode.Auto)]
    [InlineData(AgentKind.Codex, AgentAuthMode.ApiKey)]
    [InlineData(AgentKind.Cursor, AgentAuthMode.Subscription)]
    [InlineData(AgentKind.OpenCode, AgentAuthMode.Subscription)]
    public void AProviderAccountSessionMayChooseAModel(AgentKind agent, AgentAuthMode mode)
    {
        Assert.Equal((null, "claude-fable-5-1[1m]"), AgentConfiguration.ValidateModelEndpoint(agent, mode, null, " claude-fable-5-1[1m] "));
        Assert.Equal((null, null), AgentConfiguration.ValidateModelEndpoint(agent, mode, null, ""));
        // The endpoint URL stays a CustomEndpoint field.
        Assert.Throws<ArgumentException>(() => AgentConfiguration.ValidateModelEndpoint(agent, mode, "https://llm.example.com/v1", null));
    }

    [Theory]
    [InlineData("two words")]
    [InlineData("--dangerous")]
    [InlineData("tab\there")]
    public void UnsafeModelIdsAreRefused(string model) =>
        Assert.Throws<ArgumentException>(() => AgentConfiguration.ValidateModelEndpoint(AgentKind.Cursor, AgentAuthMode.Subscription, null, model));

    [Fact]
    public void OpenClawCannotChooseAModel() =>
        Assert.Contains("OpenClaw", Assert.Throws<ArgumentException>(() =>
            AgentConfiguration.ValidateModelEndpoint(AgentKind.OpenClaw, AgentAuthMode.ApiKey, null, "some-model")).Message);

    [Fact]
    public void UpdatesKeepTheModelOnlyWhileItKeepsItsMeaning()
    {
        var record = Record(AgentKind.Codex, AgentAuthMode.Subscription, "gpt-6-luna");
        Assert.Equal("gpt-6-luna", SessionUpdateValidator.ModelEndpointAfterUpdate(record, new UpdateSessionRequest { Title = "x" }).Model);
        Assert.Equal("gpt-6-luna", SessionUpdateValidator.ModelEndpointAfterUpdate(record, new UpdateSessionRequest { AuthMode = AgentAuthMode.ApiKey }).Model);
        Assert.Null(SessionUpdateValidator.ModelEndpointAfterUpdate(record, new UpdateSessionRequest { Agent = AgentKind.Claude }).Model);
        Assert.Null(SessionUpdateValidator.ModelEndpointAfterUpdate(record, new UpdateSessionRequest { ModelName = "" }).Model);
        Assert.Equal("other", SessionUpdateValidator.ModelEndpointAfterUpdate(record, new UpdateSessionRequest { ModelName = "other" }).Model);
        // Into an endpoint: a provider id is no endpoint model, so the request must name one.
        Assert.Throws<ArgumentException>(() => SessionUpdateValidator.ModelEndpointAfterUpdate(record,
            new UpdateSessionRequest { AuthMode = AgentAuthMode.CustomEndpoint, ModelEndpointUrl = "https://llm.example.com/v1" }));
        // Out of an endpoint: the endpoint's id is dropped.
        var endpoint = Record(AgentKind.Codex, AgentAuthMode.CustomEndpoint, "served-model");
        endpoint.ModelEndpointUrl = "https://llm.example.com/v1";
        Assert.Equal((null, null), SessionUpdateValidator.ModelEndpointAfterUpdate(endpoint, new UpdateSessionRequest { AuthMode = AgentAuthMode.Subscription }));
    }

    [Fact]
    public void ACopyKeepsTheModelForTheSameAgentOnly()
    {
        var source = Record(AgentKind.Claude, AgentAuthMode.Subscription, "opus");
        Assert.Equal("opus", SessionDuplication.CopyableRequest(source, new DuplicateSessionRequest("copy", null, false)).ModelName);
        Assert.Equal("opus", SessionDuplication.CopyableRequest(source,
            new DuplicateSessionRequest("copy", null, false, AuthMode: AgentAuthMode.ApiKey)).ModelName);
        Assert.Null(SessionDuplication.CopyableRequest(source,
            new DuplicateSessionRequest("copy", null, false, Agent: AgentKind.Codex, AuthMode: AgentAuthMode.Subscription)).ModelName);
    }

    [Theory]
    [InlineData(AgentKind.Claude)]
    [InlineData(AgentKind.Codex)]
    [InlineData(AgentKind.Cursor)]
    [InlineData(AgentKind.OpenCode)]
    public void ThePodIsToldTheChosenModel(AgentKind agent)
    {
        var request = new CreateSessionRequest { Agent = agent, AuthMode = AgentAuthMode.ApiKey, Mode = SessionMode.Interactive };
        var chosen = AgentPodSpecFactory.Build(Record(agent, AgentAuthMode.ApiKey, "model-x"), request, Context());
        Assert.Equal("model-x", Assert.Single(chosen.Containers[0].Env, e => e.Name == "AGENTHUB_MODEL_NAME").Value);
        var none = AgentPodSpecFactory.Build(Record(agent, AgentAuthMode.ApiKey, null), request, Context());
        Assert.DoesNotContain(none.Containers[0].Env, e => e.Name == "AGENTHUB_MODEL_NAME");
    }

    // ---------- keys and owners ----------

    [Fact]
    public void CatalogsAreKeyedByOwnerAndAccount()
    {
        var a = ModelCatalogKey.For("alice", AgentKind.Codex, AgentAuthMode.Subscription, "acct-1", null);
        Assert.NotEqual(a, ModelCatalogKey.For("bob", AgentKind.Codex, AgentAuthMode.Subscription, "acct-1", null));
        Assert.NotEqual(a, ModelCatalogKey.For("alice", AgentKind.Codex, AgentAuthMode.Subscription, "acct-2", null));
        Assert.NotEqual(a, ModelCatalogKey.For("alice", AgentKind.Claude, AgentAuthMode.Subscription, "acct-1", null));
        // Auto on a login shares the subscription list; Auto without one runs on the key.
        Assert.Equal(a with { Agent = AgentKind.Claude }, ModelCatalogKey.For("alice", AgentKind.Claude, AgentAuthMode.Auto, "acct-1", null));
        Assert.Equal(ModelCatalogKey.For("alice", AgentKind.Claude, AgentAuthMode.ApiKey, null, null),
            ModelCatalogKey.For("alice", AgentKind.Claude, AgentAuthMode.Auto, null, null));
        // Endpoints are told apart by URL, trailing slash or not.
        Assert.Equal(ModelCatalogKey.For("alice", AgentKind.Codex, AgentAuthMode.CustomEndpoint, null, "https://llm.example.com/v1/"),
            ModelCatalogKey.For("alice", AgentKind.Codex, AgentAuthMode.CustomEndpoint, "ignored", "https://llm.example.com/v1"));
        Assert.NotEqual(ModelCatalogKey.For("alice", AgentKind.Codex, AgentAuthMode.CustomEndpoint, null, "https://a.example.com/v1"),
            ModelCatalogKey.For("alice", AgentKind.Codex, AgentAuthMode.CustomEndpoint, null, "https://b.example.com/v1"));
    }

    [Fact]
    public async Task TheDialogReadsOnlyTheCallersOwnCatalog()
    {
        var store = new Store();
        var session = Record(AgentKind.Codex, AgentAuthMode.Subscription, null, owner: "alice", account: "acct-1");
        await new SessionModelCatalogs(TestPods.Live(new HttpClient(new Pod(""))), Sessions(), Config(), store)
            .RecordAsync(session, Catalog("alice-model"), default);

        var alice = Dialog(store, "alice");
        var bob = Dialog(store, "bob");
        var own = View(await alice.Read(AgentKind.Codex, AgentAuthMode.Subscription, "acct-1", null, default));
        Assert.Equal("fresh", own.Freshness);
        Assert.Equal("alice-model", Assert.Single(own.Catalog!.Models).Id);
        // Same agent, mode and even the same account id: still another person's row.
        var other = View(await bob.Read(AgentKind.Codex, AgentAuthMode.Subscription, "acct-1", null, default));
        Assert.Equal("unknown", other.Freshness);
        Assert.Null(other.Catalog);
    }

    // ---------- what is stored ----------

    [Fact]
    public void ThePodsAnswerIsRebuiltFromKnownFields()
    {
        var json = """
            {"agent":"Claude","source":"cli","verified":true,"state":"ok","email":"person@example.com",
             "account":{"organization":"Example Org"},
             "models":[{"id":"good","label":"Good","token":"sk-abc","capabilities":{"reasoningEfforts":["low","NOT OK"],"contextWindow":-5}},
                       {"id":"bad id"},{"id":"good"},{"id":"-x"}],
             "error":{"code":"Weird Code!","message":"line\u0007one"}}
            """;
        var catalog = ModelCatalogSanitizer.Parse(json)!;
        Assert.Equal(["good"], catalog.Models.Select(m => m.Id));
        Assert.Equal(["low"], catalog.Models[0].Capabilities!.ReasoningEfforts!);
        Assert.Null(catalog.Models[0].Capabilities!.ContextWindow);
        Assert.Equal("probe_failed", catalog.Error!.Code);
        Assert.Equal("lineone", catalog.Error.Message);
        var stored = JsonSerializer.Serialize(catalog, ModelCatalogSanitizer.Json);
        foreach (var leak in new[] { "person@example.com", "Example Org", "sk-abc" }) Assert.DoesNotContain(leak, stored);
        Assert.Null(ModelCatalogSanitizer.Parse("not json"));
        Assert.Null(ModelCatalogSanitizer.Parse(new string(' ', ModelCatalogSanitizer.MaxBytes + 1)));
        var many = ModelCatalogSanitizer.Clean(new ModelCatalog { Models = Enumerable.Range(0, 500).Select(i => new CatalogModel { Id = "m" + i }).ToList() });
        Assert.Equal(ModelCatalogSanitizer.MaxModels, many.Models.Count);
    }

    [Fact]
    public async Task ATransientErrorKeepsTheLastGoodListButARefusalReplacesIt()
    {
        var store = new Store();
        var catalogs = new SessionModelCatalogs(TestPods.Live(new HttpClient(new Pod(""))), Sessions(), Config(), store);
        var session = Record(AgentKind.Codex, AgentAuthMode.Subscription, null, account: "acct-1");
        await catalogs.RecordAsync(session, Catalog("m-1"), default);
        await catalogs.RecordAsync(session, new ModelCatalog { State = "error", Error = new("timeout", "slow") }, default);
        Assert.Equal("ok", (await store.GetAsync(ModelCatalogKey.ForSession(session)))!.Catalog.State);
        await catalogs.RecordAsync(session, new ModelCatalog { State = "error", Error = new("not_entitled", "refused") }, default);
        Assert.Equal("not_entitled", (await store.GetAsync(ModelCatalogKey.ForSession(session)))!.Catalog.Error!.Code);
    }

    [Fact]
    public void FreshnessAndTheSelectedModel()
    {
        var now = DateTimeOffset.Parse("2026-11-07T12:00:00Z");
        var stored = new StoredModelCatalog(Catalog("a", "b"), now.AddHours(-2));
        Assert.Equal("fresh", ModelCatalogSanitizer.View(stored, now).Freshness);
        Assert.Equal("stale", ModelCatalogSanitizer.View(stored with { FetchedAt = now.AddDays(-2) }, now).Freshness);
        Assert.Equal("unknown", ModelCatalogSanitizer.View(null, now, "a").Freshness);
        Assert.True(ModelCatalogSanitizer.View(stored, now, "b").SelectedListed);
        // A chosen model missing from the list stays chosen; the view only says so.
        var missing = ModelCatalogSanitizer.View(stored, now, "gone");
        Assert.Equal("gone", missing.Selected);
        Assert.False(missing.SelectedListed);
    }

    // ---------- who may read and refresh ----------

    [Theory]
    [InlineData(SessionAccessLevel.Owner, true)]
    [InlineData(SessionAccessLevel.Collaborator, true)]
    [InlineData(SessionAccessLevel.Viewer, false)]
    public async Task ViewersReadTheStoredListAndNeverStartAProbe(SessionAccessLevel level, bool mayProbe)
    {
        var store = new Store();
        var session = Record(AgentKind.Codex, AgentAuthMode.Subscription, "m-1", account: "acct-secret-id");
        await store.PutAsync(ModelCatalogKey.ForSession(session), Catalog("m-1"), DateTimeOffset.UtcNow);
        var probes = new Probes();
        var controller = new SessionModelsController(new Access { Resolved = new(session, level, null) }, probes, store)
        { ControllerContext = UserContext("someone") };

        var view = View(await controller.Read("s1", refresh: false, default));
        Assert.Equal("m-1", view.Selected);
        Assert.True(view.SelectedListed);
        Assert.Equal(0, probes.Calls);
        // The response names no account, owner or credential.
        var body = JsonSerializer.Serialize(view, ModelCatalogSanitizer.Json);
        Assert.DoesNotContain("acct-secret-id", body);
        Assert.DoesNotContain("owner", body);

        var refreshed = await controller.Read("s1", refresh: true, default);
        if (mayProbe) { Assert.IsType<OkObjectResult>(refreshed); Assert.Equal(1, probes.Calls); }
        else { Assert.Equal(403, Assert.IsType<ObjectResult>(refreshed).StatusCode); Assert.Equal(0, probes.Calls); }
        Assert.Equal("no-store", controller.Response.Headers.CacheControl);
    }

    [Fact]
    public async Task NoAccessIsNotFound()
    {
        var controller = new SessionModelsController(new Access(), new Probes(), new Store()) { ControllerContext = UserContext("x") };
        Assert.IsType<NotFoundResult>(await controller.Read("s1", false, default));
    }

    // ---------- the pod is the only thing asked ----------

    [Fact]
    public async Task TheProbeAsksThePodNeverTheEndpoint()
    {
        var pod = new Pod("""{"source":"endpoint","verified":true,"state":"ok","models":[{"id":"served-a"}]}""");
        var store = new Store();
        var catalogs = new SessionModelCatalogs(TestPods.Live(new HttpClient(pod)), Sessions(running: true), Config(), store);
        var session = Record(AgentKind.Codex, AgentAuthMode.CustomEndpoint, "served-a");
        session.ModelEndpointUrl = "https://llm.example.com/v1";

        var catalog = await catalogs.ProbeAsync(session, refresh: true, default);

        Assert.Equal("served-a", Assert.Single(catalog!.Models).Id);
        var request = Assert.Single(pod.Requests);
        Assert.Equal("http://127.0.0.1:7681/agenthub/models?refresh=1", request.Uri);
        Assert.Equal("secret", request.Token);
        Assert.DoesNotContain(pod.Requests, r => r.Uri.Contains("llm.example.com"));
        Assert.NotNull(await store.GetAsync(ModelCatalogKey.For("owner", AgentKind.Codex, AgentAuthMode.CustomEndpoint, null, "https://llm.example.com/v1")));
    }

    [Fact]
    public async Task AStoppedPodIsNotAskedAndAnOddAnswerIsNotStored()
    {
        var pod = new Pod("<html>");
        var store = new Store();
        var session = Record(AgentKind.Codex, AgentAuthMode.Subscription, null);
        Assert.Null(await new SessionModelCatalogs(TestPods.Live(new HttpClient(pod)), Sessions(running: false), Config(), store).ProbeAsync(session, false, default));
        Assert.Empty(pod.Requests);
        Assert.Null(await new SessionModelCatalogs(TestPods.Live(new HttpClient(pod)), Sessions(running: true), Config(), store).ProbeAsync(session, false, default));
        Assert.Null(await store.GetAsync(ModelCatalogKey.ForSession(session)));
    }

    [Fact]
    public async Task DialogRefreshUsesARunningSessionOnTheSameAccountOrSaysThereIsNone()
    {
        var store = new Store();
        var probes = new Probes { Store = store };
        var matching = Record(AgentKind.Codex, AgentAuthMode.Subscription, null, owner: "alice", account: "acct-1");
        var otherAccount = Record(AgentKind.Codex, AgentAuthMode.Subscription, null, owner: "alice", account: "acct-2");
        var sessions = SessionList(otherAccount, matching);
        var controller = new ModelCatalogController(store, sessions, probes) { ControllerContext = UserContext("alice") };

        var ok = View(await controller.Refresh(AgentKind.Codex, AgentAuthMode.Subscription, "acct-1", null, default));
        Assert.Equal("fresh", ok.Freshness);
        Assert.Equal([matching.Id], probes.Sessions);

        var none = await controller.Refresh(AgentKind.Codex, AgentAuthMode.Subscription, "acct-9", null, default);
        Assert.Equal(409, Assert.IsType<ConflictObjectResult>(none).StatusCode);
    }

    // ---------- fakes ----------

    private static ModelCatalog Catalog(params string[] ids) =>
        new() { State = "ok", Verified = true, Models = ids.Select(id => new CatalogModel { Id = id }).ToList() };

    private static ModelCatalogView View(IActionResult result) => Assert.IsType<ModelCatalogView>(Assert.IsType<OkObjectResult>(result).Value);

    private static SessionRecord Record(AgentKind agent, AgentAuthMode auth, string? model, string owner = "owner", string? account = null) => new()
    {
        Id = "s-" + Guid.NewGuid().ToString("N")[..8], Owner = owner, Title = "Session", Mode = SessionMode.Interactive,
        Agent = agent, AuthMode = auth, ModelName = model, ResolvedCredentialId = account, CallbackToken = "secret"
    };

    private static ModelCatalogController Dialog(Store store, string user) =>
        new(store, SessionList(), new Probes()) { ControllerContext = UserContext(user) };

    private static ControllerContext UserContext(string user) => new()
    {
        HttpContext = new DefaultHttpContext { User = new ClaimsPrincipal(new ClaimsIdentity([new Claim("preferred_username", user)], "test")) }
    };

    private static IConfiguration Config() => new ConfigurationBuilder().Build();

    private static PodBuildContext Context() => new()
    {
        Owner = "owner", CredentialsSecretName = "creds-owner", ClaudeCredentialSecretName = "claude-owner",
        CodexCredentialSecretName = "codex-owner", CursorCredentialSecretName = "cursor-owner",
        OpenClawCredentialSecretName = "openclaw-owner", OpenCodeCredentialSecretName = "opencode-owner",
        CallbackUrl = "http://callback/internal/sessions/session-id", StatePutUrl = "http://s3/state-put", StateGetUrl = "",
        ScrollbackPutUrl = "http://s3/scroll-put", TranscriptPutUrl = "http://s3/transcript-put",
        RuntimeImages = new AgentRuntimeImages("runtime-claude", "runtime-codex", "runtime-cursor", "runtime-openclaw", "runtime-opencode", "Always")
    };

    private sealed class Store : IModelCatalogStore
    {
        private readonly Dictionary<ModelCatalogKey, StoredModelCatalog> _rows = [];
        public Task<StoredModelCatalog?> GetAsync(ModelCatalogKey key, CancellationToken ct = default) =>
            Task.FromResult(_rows.TryGetValue(key, out var row) ? row : null);
        public Task PutAsync(ModelCatalogKey key, ModelCatalog catalog, DateTimeOffset fetchedAt, CancellationToken ct = default)
        { _rows[key] = new(catalog, fetchedAt); return Task.CompletedTask; }
    }

    private sealed class Probes : ISessionModelCatalogs
    {
        public int Calls;
        public Store? Store;
        public List<string> Sessions = [];
        public Task RecordAsync(SessionRecord session, ModelCatalog catalog, CancellationToken ct) => Task.CompletedTask;
        public async Task<ModelCatalog?> ProbeAsync(SessionRecord session, bool refresh, CancellationToken ct)
        {
            Calls++;
            Sessions.Add(session.Id);
            var catalog = Catalog("probed");
            if (Store is not null) await Store.PutAsync(ModelCatalogKey.ForSession(session), catalog, DateTimeOffset.UtcNow, ct);
            return catalog;
        }
    }

    private sealed class Access : ISessionAccessService
    {
        public SessionAccessResult? Resolved;
        public Task<SessionAccessResult?> ResolveUserAsync(string principal, string id, CancellationToken ct = default) => Task.FromResult(Resolved);
        public Task<SessionAccessResult?> ResolveTokenAsync(string token, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<SessionAccessResult?> ResolveTokenReadOnlyAsync(string token, CancellationToken ct = default) => throw new NotSupportedException();
    }

    private sealed class Pod(string body) : HttpMessageHandler
    {
        public List<(string Uri, string Token)> Requests = [];
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Requests.Add((request.RequestUri!.ToString(), request.Headers.GetValues("X-Agent-Token").Single()));
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body) });
        }
    }

    public class LiveProxy : DispatchProxy
    {
        public bool Running;
        protected override object? Invoke(MethodInfo? method, object?[]? args) =>
            method!.Name == nameof(ISessionService.GetSessionAsync)
                ? Task.FromResult<SessionInfo?>(Running
                    ? new SessionInfo { Id = (string)args![1]!, Owner = "owner", Title = "S", Mode = SessionMode.Interactive, Phase = "Running", PodIp = "127.0.0.1" }
                    : null)
                : throw new NotSupportedException(method.Name);
    }

    private static ISessionService Sessions(bool running = false)
    {
        var proxy = DispatchProxy.Create<ISessionService, LiveProxy>();
        ((LiveProxy)proxy).Running = running;
        return proxy;
    }

    public class ListProxy : DispatchProxy
    {
        public IReadOnlyList<SessionRecord> Records = [];
        protected override object? Invoke(MethodInfo? method, object?[]? args) =>
            method!.Name == nameof(ISessionStore.ListAsync)
                ? Task.FromResult<IReadOnlyList<SessionRecord>>(Records.Where(r => r.Owner == (string)args![0]!).ToList())
                : throw new NotSupportedException(method.Name);
    }

    private static ISessionStore SessionList(params SessionRecord[] records)
    {
        var proxy = DispatchProxy.Create<ISessionStore, ListProxy>();
        ((ListProxy)proxy).Records = records;
        return proxy;
    }
}
