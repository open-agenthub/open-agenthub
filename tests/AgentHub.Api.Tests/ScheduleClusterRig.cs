using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AgentHub.Api.Agents;
using AgentHub.Api.Browser;
using AgentHub.Api.Library;
using AgentHub.Api.Models;
using AgentHub.Api.Persistence;
using AgentHub.Api.Services;
using AgentHub.Api.Storage;
using AgentHub.Api.Usage;
using k8s;
using k8s.Models;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace AgentHub.Api.Tests;

public enum ScheduleFault { ListRuns, StoreRecord, WriteSecretCopies, ReplaceTemplate }

/// <summary>A stateful stand-in for the cluster API around one scheduled session "sid": its CronJob
/// (merge and JSON patches, with resourceVersion), runs, pods and secrets, with a fault switch per
/// step. Used by the scheduled move (#295) and template refresh (#301) tests.</summary>
internal sealed class ScheduleClusterRig : IAsyncDisposable
{
    private WebApplication _app = null!;
    private Kubernetes _cluster = null!;
    public KubernetesSessionService Service = null!;
    public SessionRecord Record = null!;
    public ScheduleFault? Fault;
    public bool ActiveRun;
    // The session is hidden while Start stores the secrets, so their syncs leave the template and
    // copies as the test set them up.
    private bool _ready;
    /// <summary>Refuses deleting the session's secret copies, on top of any <see cref="Fault"/>.</summary>
    public bool RefuseCopyDeletes;
    /// <summary>How many template patches answer 409 while <see cref="ScheduleFault.ReplaceTemplate"/> is set; null = all.</summary>
    public int? FaultsLeft;
    public readonly List<string> Deleted = [];
    private JsonObject _cron = null!;
    private int _version = 1;
    private readonly Dictionary<string, V1Secret> _secrets = new();
    private readonly Dictionary<string, ProjectInfo> _projects = new();

    public bool Suspended => _cron["spec"]?["suspend"]?.GetValue<bool>() == true;
    public IReadOnlyList<string> MoveAnnotations => (_cron["metadata"]!["annotations"] as JsonObject)?
        .Select(a => a.Key).Where(k => k.StartsWith("agenthub.dev/project-move")).ToList() ?? [];
    public IReadOnlyList<string> TemplateEnvNames => _cron["spec"]!["jobTemplate"]!["spec"]!["template"]!["spec"]!["containers"]!
        .AsArray().Single(c => c!["name"]!.GetValue<string>() == "agent")!["env"]!.AsArray().Select(e => e!["name"]!.GetValue<string>()).ToList();
    /// <summary>The template's reference for <paramref name="name"/> into the variable copy: what a run's pod resolves.</summary>
    public (string Secret, string Key, bool Optional) TemplateReference(string name)
    {
        var reference = _cron["spec"]!["jobTemplate"]!["spec"]!["template"]!["spec"]!["containers"]!.AsArray()
            .Single(c => c!["name"]!.GetValue<string>() == "agent")!["env"]!.AsArray()
            .Single(e => e!["name"]!.GetValue<string>() == name)!["valueFrom"]!["secretKeyRef"]!;
        return (reference["name"]!.GetValue<string>(), reference["key"]!.GetValue<string>(), reference["optional"]?.GetValue<bool>() == true);
    }
    public string? TemplateEnvValue(string name) => _cron["spec"]!["jobTemplate"]!["spec"]!["template"]!["spec"]!["containers"]!
        .AsArray().Single(c => c!["name"]!.GetValue<string>() == "agent")!["env"]!.AsArray()
        .Single(e => e!["name"]!.GetValue<string>() == name)!["value"]?.GetValue<string>();
    public bool HasSecret(string name) => _secrets.ContainsKey(name);
    public Dictionary<string, string> EnvCopy => _secrets[SessionSecretDeliveryFactory.EnvSecretName("sid")].Data?
        .ToDictionary(d => d.Key, d => Encoding.UTF8.GetString(d.Value)) ?? [];

    public static async Task<ScheduleClusterRig> Start(bool suspended = false)
    {
        var rig = new ScheduleClusterRig();
        var spec = SessionSecretPodSpecTests.Build(withSecrets: true, envNames: ["OLD_NAME"]);
        var cron = new V1CronJob { ApiVersion = "batch/v1", Kind = "CronJob",
            Metadata = new() { Name = "session-sid", NamespaceProperty = "proof",
                Labels = new Dictionary<string, string> { ["agenthub.dev/session"] = "sid", ["agenthub.dev/component"] = "cronjob" }, Annotations = new Dictionary<string, string> { ["agenthub.dev/title"] = "s" } },
            Spec = new() { Schedule = "0 3 * * *", Suspend = suspended, JobTemplate = new() { Spec = new() { Template = new() { Spec = spec } } } } };
        rig._cron = JsonNode.Parse(KubernetesJson.Serialize(cron))!.AsObject();
        rig.Stamp();
        rig.Record = new() { Id = "sid", Owner = "owner", CallbackToken = "t", Mode = SessionMode.Scheduled,
            Status = "Scheduled", Schedule = "0 3 * * *", ProjectId = "source", Agent = AgentKind.Claude, AuthMode = AgentAuthMode.ApiKey };

        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0"); builder.Logging.ClearProviders();
        rig._app = builder.Build();
        rig.MapCluster(rig._app);
        await rig._app.StartAsync();
        rig._cluster = new Kubernetes(new KubernetesClientConfiguration { Host = rig._app.Urls.Single() });
        rig.Service = rig.CreateService();
        var old = await rig.Service.UpsertSessionSecretAsync("owner", new("OLD_NAME", "env", "old-value", null));
        var neu = await rig.Service.UpsertSessionSecretAsync("owner", new("NEW_NAME", "env", "new-value", null));
        rig._projects["source"] = new("source", "Source", null, 0, [old.Id]);
        rig._projects["target"] = new("target", "Target", null, 1, [neu.Id]);
        rig._ready = true;
        return rig;
    }

    private void Stamp() => _cron["metadata"]!["resourceVersion"] = (_version++).ToString();

    private IResult Fail(ScheduleFault step) => Results.Json(new V1Status { Code = 500, Message = step + " failed" }, statusCode: 500);

    private void MapCluster(WebApplication app)
    {
        const string cronPath = "/apis/batch/v1/namespaces/{ns}/cronjobs/{name}";
        app.MapGet(cronPath, () => Results.Text(_cron.ToJsonString(), "application/json"));
        app.MapGet("/apis/batch/v1/namespaces/{ns}/cronjobs", () => Results.Text(
            new JsonObject { ["apiVersion"] = "batch/v1", ["kind"] = "CronJobList", ["items"] = new JsonArray(_cron.DeepClone()) }.ToJsonString(), "application/json"));
        app.MapMethods(cronPath, ["PATCH"], async (HttpRequest request) =>
        {
            using var reader = new StreamReader(request.Body);
            var body = JsonNode.Parse(await reader.ReadToEndAsync())!;
            if (request.ContentType!.Contains("json-patch"))
            {
                if (Fault == ScheduleFault.ReplaceTemplate && (FaultsLeft is null || FaultsLeft-- > 0)) return Results.Json(new V1Status { Code = 409, Reason = "Conflict" }, statusCode: 409);
                foreach (var op in body.AsArray()) Apply(op!.AsObject());
            }
            else Merge(_cron, body.AsObject());
            Stamp();
            return Results.Text(_cron.ToJsonString(), "application/json");
        });
        app.MapGet("/apis/batch/v1/namespaces/{ns}/jobs", () => Fault == ScheduleFault.ListRuns ? Fail(ScheduleFault.ListRuns)
            : Results.Json(new V1JobList { ApiVersion = "batch/v1", Kind = "JobList", Items = ActiveRun ? [new V1Job { Metadata = new() { Name = "run-1",
                OwnerReferences = [new V1OwnerReference { Kind = "CronJob", Name = "session-sid", ApiVersion = "batch/v1", Uid = "u" }] } }] : [] }));
        app.MapDelete("/apis/batch/v1/namespaces/{ns}/jobs/{name}", (string name) => { Deleted.Add("job/" + name); return Results.Json(new V1Status { Code = 200 }); });
        app.MapGet("/api/v1/namespaces/{ns}/pods", () => Results.Json(new V1PodList { ApiVersion = "v1", Kind = "PodList", Items = ActiveRun
            ? [new V1Pod { Metadata = new() { Name = "run-1-pod" }, Status = new() { Phase = "Pending" } }] : [] }));
        app.MapDelete("/api/v1/namespaces/{ns}/pods/{name}", (string name) =>
        {
            Deleted.Add("pod/" + name); ActiveRun = false;
            return Results.Json(new V1Pod { ApiVersion = "v1", Kind = "Pod" });
        });
        app.MapGet("/api/v1/namespaces/{ns}/pods/{name}", () => Results.NotFound());
        app.MapGet("/api/v1/namespaces/{ns}/secrets/{name}", (string name) => _secrets.TryGetValue(name, out var s) ? Results.Json(s) : Results.NotFound());
        app.MapGet("/api/v1/namespaces/{ns}/secrets", (string? labelSelector) => Results.Json(new V1SecretList { ApiVersion = "v1", Kind = "SecretList",
            Items = _secrets.Values.Where(secret => Matches(secret.Metadata.Labels, labelSelector)).ToList() }));
        app.MapPost("/api/v1/namespaces/{ns}/secrets", (V1Secret secret) => Write(secret, create: true));
        app.MapPut("/api/v1/namespaces/{ns}/secrets/{name}", (V1Secret secret) => Write(secret, create: false));
        app.MapDelete("/api/v1/namespaces/{ns}/secrets/{name}", (string name) =>
        {
            if (RefuseCopyDeletes && name.StartsWith("session")) return Results.Json(new V1Status { Code = 403, Reason = "Forbidden" }, statusCode: 403);
            return _secrets.Remove(name) ? Results.Json(new V1Status { Code = 200 }) : Results.Json(new V1Status { Code = 404 }, statusCode: 404);
        });
    }

    private static bool Matches(IDictionary<string, string>? labels, string? selector) =>
        string.IsNullOrEmpty(selector) || selector.Split(",").Select(term => term.Split("=")).All(kv => labels?.TryGetValue(kv[0], out var value) == true && value == kv[1]);

    private IResult Write(V1Secret secret, bool create)
    {
        if (Fault == ScheduleFault.WriteSecretCopies && secret.Metadata.Name.StartsWith("session")) return Fail(ScheduleFault.WriteSecretCopies);
        if (create && _secrets.ContainsKey(secret.Metadata.Name)) return Results.Json(new V1Status { Code = 409 }, statusCode: 409);
        secret.ApiVersion = "v1"; secret.Kind = "Secret";
        secret.Metadata.ResourceVersion = (_version++).ToString();
        _secrets[secret.Metadata.Name] = secret;
        return Results.Json(secret);
    }

    private void Apply(JsonObject op)
    {
        var path = op["path"]!.GetValue<string>().Split('/').Skip(1).Select(p => p.Replace("~1", "/").Replace("~0", "~")).ToArray();
        JsonNode parent = _cron;
        foreach (var part in path[..^1]) parent = parent is JsonArray array ? array[int.Parse(part)]! : parent[part]!;
        var key = path[^1];
        switch (op["op"]!.GetValue<string>())
        {
            case "test":
                if (parent[key]!.ToJsonString() != op["value"]!.ToJsonString()) throw new InvalidOperationException("test failed");
                break;
            case "remove": parent.AsObject().Remove(key); break;
            default:
                if (parent is JsonArray list) list[int.Parse(key)] = op["value"]!.DeepClone();
                else parent[key] = op["value"]!.DeepClone();
                break;
        }
    }

    private static void Merge(JsonObject target, JsonObject patch)
    {
        foreach (var (key, value) in patch.ToList())
        {
            if (value is null) target.Remove(key);
            else if (value is JsonObject child && target[key] is JsonObject existing) Merge(existing, child);
            else target[key] = value.DeepClone();
        }
    }

    private KubernetesSessionService CreateService()
    {
        var store = RuntimeProofProxy.Make<ISessionStore>((method, args) => method.Name switch
        {
            "GetAsync" => Task.FromResult(_ready && args![0] as string == Record.Owner && args[1] as string == Record.Id ? Copy(Record) : null),
            "ListAsync" => Task.FromResult<IReadOnlyList<SessionRecord>>(_ready ? [Copy(Record)] : []),
            "UpsertAsync" => Fault == ScheduleFault.StoreRecord ? Task.FromException(new InvalidOperationException("store failed"))
                : Store((SessionRecord)args![0]!),
            _ => null
        });
        var projects = RuntimeProofProxy.Make<IProjectStore>((method, args) => method.Name switch
        {
            "GetAsync" => Task.FromResult(_projects.GetValueOrDefault((string)args![1]!)),
            "ListAsync" => Task.FromResult<IReadOnlyList<ProjectInfo>>(_projects.Values.ToList()),
            _ => null
        });
        var browsers = RuntimeProofProxy.Make<IBrowserService>((method, args) => method.Name switch
        {
            "GetSummaryAsync" => Task.FromResult(BrowserSummary.Stopped),
            "GetSummariesAsync" => Task.FromResult<IReadOnlyDictionary<string, BrowserSummary>>(
                ((IEnumerable<string>)args![0]!).ToDictionary(id => id, _ => BrowserSummary.Stopped)),
            _ => null
        });
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["AgentHub:Namespace"] = "proof", ["AgentHub:SkillLibraryMcp"] = "false"
        }).Build();
        return new(config, store, projects, RuntimeProofProxy.Make<IArtifactStore>(), browsers, RuntimeProofProxy.Make<IGitAuthService>(),
            new UsageLimitService(RuntimeProofProxy.Make<IUsageStore>(), RuntimeProofProxy.Make<IPersonalUsageLimitSource>(), []),
            new AllowAllAgentsProvider(), null!, null!, new InMemoryEphemeralApiMcpStore(), null!,
            NullLogger<KubernetesSessionService>.Instance, runtimeUpdateLock: new OpenLock(), cluster: _cluster);
    }

    private Task Store(SessionRecord record)
    {
        // A copy, as Postgres would hold it: the service's in-memory edits must not count as stored.
        Record = Copy(record);
        return Task.CompletedTask;
    }

    private static SessionRecord Copy(SessionRecord record) => JsonSerializer.Deserialize<SessionRecord>(JsonSerializer.Serialize(record))!;

    public async ValueTask DisposeAsync() { _cluster.Dispose(); await _app.DisposeAsync(); }

    /// <summary>The copies a spawn writes, so a sync has this session to bring up to date.</summary>
    public void SeedCopies(params string[] envNames)
    {
        var labels = new Dictionary<string, string> { ["agenthub.dev/owner"] = KubernetesSessionService.OwnerKey("owner"), ["agenthub.dev/session"] = "sid" };
        _secrets[SessionSecretDeliveryFactory.FilesSecretName("sid")] = new V1Secret { ApiVersion = "v1", Kind = "Secret",
            Metadata = new() { Name = SessionSecretDeliveryFactory.FilesSecretName("sid"), ResourceVersion = "1",
                Labels = new Dictionary<string, string>(labels) { ["agenthub.dev/component"] = SessionSecretDeliveryFactory.FilesComponent } } };
        _secrets[SessionSecretDeliveryFactory.EnvSecretName("sid")] = new V1Secret { ApiVersion = "v1", Kind = "Secret",
            Metadata = new() { Name = SessionSecretDeliveryFactory.EnvSecretName("sid"), ResourceVersion = "1",
                Labels = new Dictionary<string, string>(labels) { ["agenthub.dev/component"] = SessionSecretDeliveryFactory.EnvComponent } },
            Data = envNames.ToDictionary(n => n, _ => Encoding.UTF8.GetBytes("seeded")) };
    }

    public void Annotate(string key, string value) =>
        ((_cron["metadata"]!["annotations"] ??= new JsonObject()) as JsonObject)![key] = value;

    public ProjectInfo Project(string id) => _projects[id];
    public void SetProject(ProjectInfo project) => _projects[project.Id] = project;

    private sealed class OpenLock : ISessionExpiryLock
    {
        public Task<IAsyncDisposable?> TryAcquireAsync(string id, CancellationToken ct = default) => Task.FromResult<IAsyncDisposable?>(new Held());
        private sealed class Held : IAsyncDisposable { public ValueTask DisposeAsync() => ValueTask.CompletedTask; }
    }
}
