using System.Net;
using System.Net.Http.Json;
using System.Reflection;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using AgentHub.Api.Agents;
using AgentHub.Api.Browser;
using AgentHub.Api.Controllers;
using AgentHub.Api.Models;
using AgentHub.Api.Library;
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

public class ManualRuntimeUpdateTests
{
    [Theory]
    [InlineData(AgentKind.Codex, "terminal", false, 404)]
    [InlineData(AgentKind.Claude, "chat", false, 404)]
    [InlineData(AgentKind.Claude, "chat", true, 200)]
    [InlineData(AgentKind.Claude, "chat", false, 409)]
    public async Task ConfirmationPausesThenResumesCurrentImageUnderOneLock(
        AgentKind agent, string ui, bool question, int drainStatus)
    {
        await using var rig = await Rig.Start(agent, ui, question, drainStatus);
        var refusal = await rig.Http.PostAsync("/api/sessions/session/update-runtime", null);
        Assert.Equal(HttpStatusCode.Conflict, refusal.StatusCode);
        Assert.Equal("runtime_update_confirmation_required", (await refusal.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString());
        Assert.False(rig.Deleting);
        rig.Http.DefaultRequestHeaders.Add("X-Owner", "viewer");
        Assert.Equal(HttpStatusCode.NotFound, (await rig.Http.PostAsync("/api/sessions/session/pause-and-resume-runtime", null)).StatusCode);
        rig.Http.DefaultRequestHeaders.Remove("X-Owner");
        var update = rig.Http.PostAsync("/api/sessions/session/pause-and-resume-runtime", null);
        await rig.DeleteStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Null(rig.CreatedPod);
        Assert.Equal(HttpStatusCode.Conflict, (await rig.Http.PostAsync("/api/sessions/session/pause-and-resume-runtime", null)).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await rig.Http.PostAsync("/api/sessions/session/pause", null)).StatusCode);
        rig.OldPodGone = true;
        var response = await update;
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("current", Assert.Single(rig.CreatedPod!.Spec.Containers, c => c.Name == "agent").Image);
        var env = rig.CreatedPod.Spec.Containers.First(c => c.Name == "agent").Env;
        Assert.Contains(env, e => e.Name == "AGENTHUB_RESUME" && e.Value == "1");
        Assert.Contains(env, e => e.Name == "AGENTHUB_STATE_GET_URL" && e.Value!.Contains("state.tgz"));
        Assert.Equal("conversation", rig.Record.AgentSessionId);
        Assert.False(rig.Record.QuestionPending);
        Assert.True(rig.ArchiveCheckedAfterDeletion);
        Assert.Equal(37, rig.DeleteGrace);
    }

    [Fact]
    public async Task MissingArchiveLeavesPausedAndDoesNotSpawn()
    {
        await using var rig = await Rig.Start(AgentKind.Codex, "terminal", false, 404);
        rig.ArchiveAvailable = false;
        var update = rig.Http.PostAsync("/api/sessions/session/pause-and-resume-runtime", null);
        await rig.DeleteStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));
        rig.OldPodGone = true;
        Assert.Equal(HttpStatusCode.Conflict, (await update).StatusCode);
        Assert.Equal(SessionStatus.Paused, rig.Record.Status);
        Assert.Null(rig.CreatedPod);
    }

    [Theory]
    [InlineData(SessionMode.Autonomous, null, true)]
    [InlineData(SessionMode.Scheduled, null, true)]
    [InlineData(SessionMode.Interactive, "custom", true)]
    [InlineData(SessionMode.Interactive, null, false)]
    public async Task ConfirmationDoesNotOverrideRestartRestrictions(SessionMode mode, string? image, bool storage)
    {
        await using var rig = await Rig.Start(AgentKind.Codex, "terminal", false, 404);
        rig.Record.Mode = mode; rig.Record.Image = image; rig.StorageConfigured = storage;
        Assert.Equal(HttpStatusCode.Conflict, (await rig.Http.PostAsync("/api/sessions/session/pause-and-resume-runtime", null)).StatusCode);
        Assert.False(rig.Deleting);
    }

    private sealed class Rig : IAsyncDisposable
    {
        private WebApplication _app = null!;
        private Kubernetes _cluster = null!;
        public HttpClient Http = null!;
        public SessionRecord Record = null!;
        public volatile bool Deleting, OldPodGone;
        public bool ArchiveAvailable = true, StorageConfigured = true, ArchiveCheckedAfterDeletion;
        public int DeleteGrace;
        public V1Pod? CreatedPod;
        public TaskCompletionSource DeleteStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public static async Task<Rig> Start(AgentKind agent, string ui, bool question, int drainStatus)
        {
            var rig = new Rig { Record = new() { Id = "session", Owner = "owner", CallbackToken = "callback",
                AgentSessionId = "conversation", Agent = agent, AuthMode = AgentAuthMode.ApiKey,
                Mode = SessionMode.Interactive, UiMode = ui, QuestionPending = question, Status = "Running" } };
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
            rig._app.MapPost("/agenthub/runtime-drain", () => Results.StatusCode(drainStatus));
            rig._app.MapDelete("/agenthub/runtime-drain", () => Results.Ok());
            rig._app.MapGet("/api/v1/namespaces/{ns}/pods/{name}", () => rig.OldPodGone
                ? Results.NotFound() : Results.Json(new V1Pod { ApiVersion = "v1", Kind = "Pod", Metadata = new() { Name = "session-session", Uid = "old" },
                    Spec = new() { Containers = [new() { Name = "agent", Image = "old" }] }, Status = new() { Phase = "Running", PodIP = "127.0.0.1" } }));
            rig._app.MapDelete("/api/v1/namespaces/{ns}/pods/{name}", (HttpRequest request) => {
                if (!rig.OldPodGone) { rig.Deleting = true; rig.DeleteGrace = int.Parse(request.Query["gracePeriodSeconds"]!); rig.DeleteStarted.TrySetResult(); }
                return Results.Json(new V1Pod { ApiVersion = "v1", Kind = "Pod" });
            });
            rig._app.MapPost("/api/v1/namespaces/{ns}/pods", async (HttpRequest request) => {
                rig.CreatedPod = KubernetesJson.Deserialize<V1Pod>(await new StreamReader(request.Body).ReadToEndAsync());
                return Results.Json(rig.CreatedPod);
            });
            rig._app.MapGet("/api/v1/namespaces/{ns}/secrets/{name}", () => Results.Json(new V1Secret { ApiVersion = "v1", Kind = "Secret",
                Data = new Dictionary<string, byte[]> { [agent == AgentKind.Claude ? "anthropic_api_key" : "openai_api_key"] = Encoding.UTF8.GetBytes("proof-key") } }));
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
            var artifacts = RuntimeProofProxy.Make<IArtifactStore>((method, _) => method.Name switch {
                "get_IsConfigured" => StorageConfigured,
                "HeadAsync" => Head(),
                "PresignGet" or "PresignPut" => "http://example.com/state.tgz",
                _ => null
            });
            var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> {
                ["AgentHub:Namespace"] = "proof", ["AgentHub:AgentImage"] = "current", ["AgentHub:CodexAgentImage"] = "current",
                ["AgentHub:AgentPort"] = Http.BaseAddress!.Port.ToString(), ["AgentHub:PauseGracePeriodSeconds"] = "37",
                ["AgentHub:SkillLibraryMcp"] = "false"
            }).Build();
            return new(config, store, null!, artifacts, RuntimeProofProxy.Make<IBrowserService>(), RuntimeProofProxy.Make<IGitAuthService>(),
                new UsageLimitService(RuntimeProofProxy.Make<IUsageStore>(), RuntimeProofProxy.Make<IPersonalUsageLimitSource>(), []), new AllowAllAgentsProvider(), null!, null!,
                new InMemoryEphemeralApiMcpStore(), null!, NullLogger<KubernetesSessionService>.Instance, runtimeUpdateLock: new MemoryLock(), cluster: _cluster);
        }
        private Task<ArtifactObjectInfo?> Head() {
            ArchiveCheckedAfterDeletion = OldPodGone;
            return Task.FromResult(ArchiveAvailable ? new ArtifactObjectInfo(1, "application/gzip") : null);
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

public class RuntimeProofProxy : DispatchProxy
{
    public Func<MethodInfo, object?[]?, object?>? Handler;
    public static T Make<T>(Func<MethodInfo, object?[]?, object?>? handler = null) where T : class {
        var proxy = Create<T, RuntimeProofProxy>(); ((RuntimeProofProxy)(object)proxy).Handler = handler; return proxy;
    }
    protected override object? Invoke(MethodInfo? method, object?[]? args) {
        var result = Handler?.Invoke(method!, args); if (result is not null) return result;
        if (method!.ReturnType == typeof(Task)) return Task.CompletedTask;
        if (method.ReturnType.IsGenericType && method.ReturnType.GetGenericTypeDefinition() == typeof(Task<>)) {
            var type = method.ReturnType.GenericTypeArguments[0];
            return typeof(Task).GetMethod(nameof(Task.FromResult))!.MakeGenericMethod(type).Invoke(null, [type.IsValueType ? Activator.CreateInstance(type) : null]);
        }
        return null;
    }
}
