using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text;
using AgentHub.Api.Agents;
using AgentHub.Api.Browser;
using AgentHub.Api.Controllers;
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
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AgentHub.Api.Tests;

/// <summary>
/// #215: per-session limits through the real sessions controller and KubernetesSessionService,
/// and POST apply-resources restarting a running session. The cluster is an HTTP fixture here, so
/// these tests prove ordering and the pod specs sent; admission itself is proven against a real
/// kube-apiserver (docs/session-resources.md).
/// </summary>
public class ApplySessionResourcesTests
{
    [Fact]
    public async Task ApplyDryRunsTheNewSpecThenPausesAndResumesWithIt()
    {
        await using var rig = await Rig.Start();
        var saved = await rig.Http.PatchAsJsonAsync("/api/sessions/session", new { memory = "2Gi", memoryLimit = "8Gi" });
        Assert.Equal(HttpStatusCode.OK, saved.StatusCode);
        var info = await saved.Content.ReadFromJsonAsync<SessionInfo>();
        Assert.True(info!.ResourcesPendingRestart);
        Assert.Equal(new EffectiveSessionResources("500m", "2", "2Gi", "8Gi"), info.Resources);
        Assert.False(rig.Deleting);

        var apply = rig.Http.PostAsync("/api/sessions/session/apply-resources", null);
        await rig.DeleteStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));
        // Asked before anything was stopped, under its own name, without the scheduler's node.
        var probe = Assert.Single(rig.DryRuns);
        Assert.Equal("session-session-resources", probe.Metadata.Name);
        Assert.Null(probe.Spec.NodeName);
        var asked = probe.Spec.Containers.Single(c => c.Name == "agent").Resources;
        Assert.Equal("8Gi", asked.Limits["memory"].ToString());
        Assert.Equal("2Gi", asked.Requests["memory"].ToString());
        Assert.Null(rig.CreatedPod);
        rig.OldPodGone = true;
        Assert.Equal(HttpStatusCode.OK, (await apply).StatusCode);

        var agent = rig.CreatedPod!.Spec.Containers.Single(c => c.Name == "agent");
        Assert.Equal("8Gi", agent.Resources.Limits["memory"].ToString());
        Assert.Equal("2", agent.Resources.Limits["cpu"].ToString());
        Assert.Equal("2Gi", agent.Resources.Requests["memory"].ToString());
        Assert.Contains(agent.Env, e => e.Name == "AGENTHUB_RESUME" && e.Value == "1");
        Assert.Equal("conversation", rig.Record.AgentSessionId);
    }

    [Fact]
    public async Task AdmissionRefusalLeavesTheSessionRunning()
    {
        await using var rig = await Rig.Start();
        rig.DryRunRefusal = (HttpStatusCode.Forbidden,
            "pods \"session-session-resources\" is forbidden: maximum memory usage per Container is 6Gi, but limit is 8Gi");
        await rig.Http.PatchAsJsonAsync("/api/sessions/session", new { memoryLimit = "8Gi" });
        var refused = await rig.Http.PostAsync("/api/sessions/session/apply-resources", null);
        Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
        var message = await refused.Content.ReadAsStringAsync();
        Assert.Contains("keeps running unchanged", message);
        Assert.Contains("maximum memory usage per Container is 6Gi", message);
        Assert.False(rig.Deleting);
        Assert.Equal("Running", rig.Record.Status);
        // The edit itself is kept: it applies at the next start.
        Assert.Equal("8Gi", rig.Record.MemoryLimit);
    }

    [Fact]
    public async Task QuotaRefusalSaysTheRunningPodStillCounts()
    {
        await using var rig = await Rig.Start();
        rig.DryRunRefusal = (HttpStatusCode.Forbidden,
            "pods \"session-session-resources\" is forbidden: exceeded quota: sessions, requested: limits.memory=8Gi, used: limits.memory=4Gi, limited: limits.memory=10Gi");
        await rig.Http.PatchAsJsonAsync("/api/sessions/session", new { memoryLimit = "8Gi" });
        var refused = await rig.Http.PostAsync("/api/sessions/session/apply-resources", null);
        Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
        Assert.Contains("still counts against the namespace quota", await refused.Content.ReadAsStringAsync());
        Assert.False(rig.Deleting);
    }

    [Fact]
    public async Task AWebhookWithoutDryRunSupportDoesNotBlockTheRestart()
    {
        await using var rig = await Rig.Start();
        rig.DryRunRefusal = (HttpStatusCode.BadRequest, "admission webhook \"policy.example.com\" does not support dry run");
        await rig.Http.PatchAsJsonAsync("/api/sessions/session", new { cpuLimit = "4" });
        var apply = rig.Http.PostAsync("/api/sessions/session/apply-resources", null);
        await rig.DeleteStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));
        rig.OldPodGone = true;
        Assert.Equal(HttpStatusCode.OK, (await apply).StatusCode);
        Assert.Equal("4", rig.CreatedPod!.Spec.Containers.Single(c => c.Name == "agent").Resources.Limits["cpu"].ToString());
    }

    [Fact]
    public async Task APodThatAlreadyHasTheValuesIsNotRestarted()
    {
        await using var rig = await Rig.Start();
        // The pod's "2000m" is the same amount as the default limit "2": nothing to apply.
        await rig.Http.PatchAsJsonAsync("/api/sessions/session", new { cpu = "500m", memory = "1Gi", cpuLimit = "", memoryLimit = "" });
        Assert.Equal(HttpStatusCode.OK, (await rig.Http.PostAsync("/api/sessions/session/apply-resources", null)).StatusCode);
        Assert.Empty(rig.DryRuns);
        Assert.False(rig.Deleting);
    }

    [Theory]
    [InlineData(SessionMode.Autonomous, true)]
    [InlineData(SessionMode.Interactive, false)]
    public async Task OnlyARunningInteractiveSessionWithStorageIsRestarted(SessionMode mode, bool storage)
    {
        await using var rig = await Rig.Start();
        rig.Record.Mode = mode; rig.StorageConfigured = storage;
        rig.Record.MemoryLimit = "8Gi";
        Assert.Equal(HttpStatusCode.Conflict, (await rig.Http.PostAsync("/api/sessions/session/apply-resources", null)).StatusCode);
        Assert.Empty(rig.DryRuns);
        Assert.False(rig.Deleting);
    }

    [Fact]
    public async Task AnotherOwnerCannotRestartTheSession()
    {
        await using var rig = await Rig.Start();
        rig.Record.MemoryLimit = "8Gi";
        rig.Http.DefaultRequestHeaders.Add("X-Owner", "viewer");
        Assert.Equal(HttpStatusCode.NotFound, (await rig.Http.PostAsync("/api/sessions/session/apply-resources", null)).StatusCode);
        Assert.False(rig.Deleting);
    }

    [Theory]
    [InlineData("{\"memoryLimit\":\"20Gi\"}", "above the ceiling of 16Gi")]
    [InlineData("{\"memoryLimit\":\"512Mi\"}", "below this session's memory request of 1Gi")]
    [InlineData("{\"cpu\":\"3\"}", "above the maximum of 2 on this instance")]
    [InlineData("{\"cpu\":\"3\",\"cpuLimit\":\"2500m\"}", "above this session's cpu limit of 2500m")]
    public async Task EditsOutsideTheBoundsAreRefusedBeforeAnythingIsStored(string body, string expected)
    {
        await using var rig = await Rig.Start();
        var response = await rig.Http.PatchAsync("/api/sessions/session", new StringContent(body, Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains(expected, await response.Content.ReadAsStringAsync());
        Assert.Null(rig.Record.MemoryLimit);
        Assert.Equal("500m", rig.Record.Cpu);
    }

    private sealed class Rig : IAsyncDisposable
    {
        private WebApplication _app = null!;
        private Kubernetes _cluster = null!;
        public HttpClient Http = null!;
        public SessionRecord Record = null!;
        public volatile bool Deleting, OldPodGone;
        public bool StorageConfigured = true;
        public (HttpStatusCode Status, string Message)? DryRunRefusal;
        public readonly List<V1Pod> DryRuns = [];
        public V1Pod? CreatedPod;
        public readonly TaskCompletionSource DeleteStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public static async Task<Rig> Start()
        {
            var rig = new Rig { Record = new() { Id = "session", Owner = "owner", CallbackToken = "callback",
                AgentSessionId = "conversation", Agent = AgentKind.Codex, AuthMode = AgentAuthMode.ApiKey,
                Mode = SessionMode.Interactive, UiMode = "terminal", Status = "Running", Cpu = "500m", Memory = "1Gi" } };
            var builder = WebApplication.CreateBuilder();
            builder.WebHost.UseUrls("http://127.0.0.1:0"); builder.Logging.ClearProviders();
            builder.Services.AddControllers().AddApplicationPart(typeof(SessionsController).Assembly).AddControllersAsServices();
            builder.Services.AddAuthorization();
            builder.Services.AddSingleton<ISessionService>(_ => rig.Service());
            builder.Services.AddTransient(services => new SessionsController(services.GetRequiredService<ISessionService>(), null!, []));
            rig._app = builder.Build();
            rig._app.Use(async (context, next) => {
                context.User = new ClaimsPrincipal(new ClaimsIdentity([new Claim("preferred_username", context.Request.Headers["X-Owner"].FirstOrDefault() ?? "owner")], "proof"));
                await next();
            });
            rig._app.UseAuthorization();
            rig._app.MapControllers();
            // The running pod as the API server returns it: scheduled onto a node, with the
            // resources the session was started with ("1" and "1000m" being the same amount).
            rig._app.MapGet("/api/v1/namespaces/{ns}/pods/{name}", () => rig.OldPodGone
                ? Results.NotFound() : Results.Json(new V1Pod { ApiVersion = "v1", Kind = "Pod",
                    Metadata = new() { Name = "session-session", Uid = "old", Labels = new Dictionary<string, string> { ["agenthub.dev/session"] = "session" } },
                    Spec = new() { NodeName = "node-a", Containers = [new() { Name = "agent", Image = "current",
                        Resources = new() {
                            Requests = new Dictionary<string, ResourceQuantity> { ["cpu"] = new("500m"), ["memory"] = new("1Gi") },
                            Limits = new Dictionary<string, ResourceQuantity> { ["cpu"] = new("2000m"), ["memory"] = new("4Gi") } } }] },
                    Status = new() { Phase = "Running", PodIP = "127.0.0.1" } }));
            rig._app.MapDelete("/api/v1/namespaces/{ns}/pods/{name}", () => {
                if (!rig.OldPodGone) { rig.Deleting = true; rig.DeleteStarted.TrySetResult(); }
                return Results.Json(new V1Pod { ApiVersion = "v1", Kind = "Pod" });
            });
            rig._app.MapPost("/api/v1/namespaces/{ns}/pods", async (HttpRequest request) => {
                using var reader = new StreamReader(request.Body);
                var pod = KubernetesJson.Deserialize<V1Pod>(await reader.ReadToEndAsync());
                if (request.Query["dryRun"] == "All")
                {
                    lock (rig.DryRuns) rig.DryRuns.Add(pod);
                    if (rig.DryRunRefusal is { } refusal)
                        return Results.Json(new { kind = "Status", apiVersion = "v1", status = "Failure", message = refusal.Message },
                            statusCode: (int)refusal.Status);
                    return Results.Json(pod, statusCode: 201);
                }
                rig.CreatedPod = pod;
                return Results.Json(pod);
            });
            rig._app.MapGet("/api/v1/namespaces/{ns}/secrets/{name}", () => Results.Json(new V1Secret { ApiVersion = "v1", Kind = "Secret",
                Data = new Dictionary<string, byte[]> { ["openai_api_key"] = Encoding.UTF8.GetBytes("proof-key") } }));
            rig._app.MapPost("/api/v1/namespaces/{ns}/secrets", () => Results.Json(new V1Secret { ApiVersion = "v1", Kind = "Secret" }));
            rig._app.MapDelete("/api/v1/namespaces/{ns}/secrets/{name}", () => Results.Json(new V1Secret { ApiVersion = "v1", Kind = "Secret" }));
            await rig._app.StartAsync();
            rig.Http = new HttpClient { BaseAddress = new Uri(rig._app.Urls.Single()) };
            rig._cluster = new Kubernetes(new KubernetesClientConfiguration { Host = rig.Http.BaseAddress.ToString() });
            return rig;
        }

        private KubernetesSessionService Service()
        {
            var store = RuntimeProofProxy.Make<ISessionStore>((method, args) => method.Name switch {
                "GetAsync" => Task.FromResult(args![0] as string == Record.Owner ? Record : null),
                "UpsertAsync" => Task.CompletedTask,
                _ => null
            });
            var artifacts = RuntimeProofProxy.Make<IArtifactStore>((method, args) => method.Name switch {
                "get_IsConfigured" => StorageConfigured,
                "HeadAsync" => Task.FromResult<ArtifactObjectInfo?>(new ArtifactObjectInfo(1, "application/gzip")),
                "PresignGet" or "PresignPut" => "http://example.com/state.tgz",
                _ => null
            });
            var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> {
                ["AgentHub:Namespace"] = "proof", ["AgentHub:CodexAgentImage"] = "current",
                ["AgentHub:AgentPort"] = Http.BaseAddress!.Port.ToString(), ["AgentHub:PauseGracePeriodSeconds"] = "1",
                ["AgentHub:SkillLibraryMcp"] = "false",
                ["AgentHub:CpuLimitCeiling"] = "8", ["AgentHub:MemoryLimitCeiling"] = "16Gi"
            }).Build();
            return new(config, store, null!, artifacts, RuntimeProofProxy.Make<IBrowserService>(), RuntimeProofProxy.Make<IGitAuthService>(),
                new UsageLimitService(RuntimeProofProxy.Make<IUsageStore>(), RuntimeProofProxy.Make<IPersonalUsageLimitSource>(), []), new AllowAllAgentsProvider(), null!, null!,
                new InMemoryEphemeralApiMcpStore(), null!, NullLogger<KubernetesSessionService>.Instance, runtimeUpdateLock: new MemoryLock(), cluster: _cluster);
        }

        public async ValueTask DisposeAsync() { OldPodGone = true; Http.Dispose(); _cluster.Dispose(); await _app.DisposeAsync(); }
    }

    private sealed class MemoryLock : ISessionExpiryLock
    {
        private int _held;
        public Task<IAsyncDisposable?> TryAcquireAsync(string id, CancellationToken ct = default) =>
            Task.FromResult<IAsyncDisposable?>(Interlocked.CompareExchange(ref _held, 1, 0) == 0 ? new Held(this) : null);
        private sealed class Held(MemoryLock owner) : IAsyncDisposable {
            public ValueTask DisposeAsync() { Interlocked.Exchange(ref owner._held, 0); return ValueTask.CompletedTask; }
        }
    }
}
