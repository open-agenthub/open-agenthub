using System.Net;
using AgentHub.Api.Controllers;
using AgentHub.Api.Otel;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using AgentHub.Api.Files;
using AgentHub.Api.Browser;
using AgentHub.Api.Models;
using AgentHub.Api.Persistence;
using AgentHub.Api.Services;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Npgsql;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace AgentHub.Api.Tests;

public class ExecutionFenceTests
{
    [PostgreSqlFact]
    public async Task RetirementWaitsForCallbacksAndRejectsStaleCredentialRequestsBeforeNetworkSend()
    {
        await using var database = await PostgresSharingDatabase.CreateAsync(pooling: false);
        await database.Sessions.UpsertAsync(new SessionRecord { Id = "s", Owner = "alice", CallbackToken = "source-token", AgentSessionId = "native" });
        var workspaces = new PostgresSessionWorkspaceStore(database.Configuration);
        await workspaces.InitializeAsync();
        await workspaces.AddAsync("alice", new("s", "workspace-s", null, "10Gi", null, "nonce", "[]"));
        var fence = new PostgresExecutionFence(database.Configuration);
        var authorizer = new AgentCallbackAuthorizer(database.Sessions, fence);
        var authorized = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finish = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var receivedCredentials = 0;
        var builder = WebApplication.CreateBuilder();
        await using var app = builder.Build();
        app.Urls.Add("http://127.0.0.1:0");
        app.MapGet("/callback", async context =>
        {
            if (await authorizer.AuthorizeAsync(context.Request, "s", context.RequestAborted) is null)
            { context.Response.StatusCode = 401; return; }
            authorized.TrySetResult();
            await finish.Task.WaitAsync(TimeSpan.FromSeconds(10));
            context.Response.StatusCode = 204;
        });
        app.MapPost("/push", context => { Interlocked.Increment(ref receivedCredentials); context.Response.StatusCode = 204; return Task.CompletedTask; });
        await app.StartAsync();
        try
        {
            using var client = new HttpClient();
            using var callback = new HttpRequestMessage(HttpMethod.Get, app.Urls.Single() + "/callback");
            callback.Headers.Add("X-Agent-Token", "source-token");
            var pendingCallback = client.SendAsync(callback);
            await authorized.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var retirement = fence.WriteAsync("s", default);
            await Task.Delay(100);
            Assert.False(retirement.IsCompleted);
            finish.TrySetResult();
            using var callbackResponse = await pendingCallback.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(HttpStatusCode.NoContent, callbackResponse.StatusCode);
            await using (var exclusive = await retirement.WaitAsync(TimeSpan.FromSeconds(5)))
            {
                Assert.NotNull(exclusive);
                await using var connection = new NpgsqlConnection(database.Configuration.GetConnectionString("Postgres"));
                await connection.OpenAsync();
                await using var change = new NpgsqlCommand("UPDATE sessions SET callback_token='target-token',execution_generation=1 WHERE id='s'", connection);
                await change.ExecuteNonQueryAsync();
            }
            using var retired = new HttpRequestMessage(HttpMethod.Get, app.Urls.Single() + "/callback");
            retired.Headers.Add("X-Agent-Token", "source-token");
            using var rejected = await client.SendAsync(retired);
            Assert.Equal(HttpStatusCode.Unauthorized, rejected.StatusCode);
            var pods = new AgentPodRequests(new HttpClient(), new LoopbackPod(), fence, database.Sessions);
            using var stalePush = new HttpRequestMessage(HttpMethod.Post, app.Urls.Single() + "/push") { Content = new StringContent("source-only-synthetic-value") };
            stalePush.Headers.Add("X-Agent-Token", "source-token");
            await Assert.ThrowsAsync<AgentPodNotConfirmedException>(() => pods.SendAsync("s", stalePush,
                HttpCompletionOption.ResponseContentRead, TimeSpan.FromSeconds(2), default));
            Assert.Equal(0, receivedCredentials);
            using var targetPush = new HttpRequestMessage(HttpMethod.Post, app.Urls.Single() + "/push") { Content = new StringContent("target-only-synthetic-value") };
            targetPush.Headers.Add("X-Agent-Token", "target-token");
            using var accepted = await pods.SendAsync("s", targetPush, HttpCompletionOption.ResponseContentRead, TimeSpan.FromSeconds(2), default);
            Assert.Equal(HttpStatusCode.NoContent, accepted.StatusCode);
            Assert.Equal(1, receivedCredentials);
        }
        finally { finish.TrySetResult(); await app.StopAsync(); }
    }
    [PostgreSqlFact]
    public async Task TelemetryIngestFinishesBeforeRotationAndRetiredCredentialCannotWriteAfterwards()
    {
        await using var database = await PostgresSharingDatabase.CreateAsync(pooling: false);
        await database.Sessions.UpsertAsync(new() { Id = "s", Owner = "alice", CallbackToken = "source-token", AgentSessionId = "native", AuthMode = AgentAuthMode.ApiKey });
        var workspaces = new PostgresSessionWorkspaceStore(database.Configuration);
        await workspaces.InitializeAsync();
        await workspaces.AddAsync("alice", new("s", "workspace-s", null, "10Gi", null, "nonce", "[]"));
        var usage = new PostgresUsageStore(database.Configuration); await usage.InitializeAsync();
        var fence = new PostgresExecutionFence(database.Configuration);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finish = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        async Task<bool> Ingest(SessionUsageDelta delta) { entered.TrySetResult(); await finish.Task; return await usage.AddDeltaAsync(delta); }
        var heldUsage = RuntimeProofProxy.Make<IUsageStore>((method, args) => method.Name == "AddDeltaAsync" ? Ingest((SessionUsageDelta)args![0]!) : null);
        var builder = WebApplication.CreateBuilder();
        await using var app = builder.Build(); app.Urls.Add("http://127.0.0.1:0");
        app.MapPost("/internal/otel/v1/metrics", async context =>
        {
            var controller = new OtelController(heldUsage, NullLogger<OtelController>.Instance, database.Sessions,
                new AgentCallbackAuthorizer(database.Sessions, fence))
                { ControllerContext = new ControllerContext { HttpContext = context } };
            await (await controller.Metrics(context.RequestAborted)).ExecuteResultAsync(new ActionContext { HttpContext = context });
        });
        await app.StartAsync();
        try
        {
            using var http = new HttpClient();
            var bytes = new OtlpMetricsParserTests.OtlpBuilder().Resource(("agenthub.session_id", "s")).TokenPoint("input", 10).Build();
            HttpRequestMessage Report(string token) { var message = new HttpRequestMessage(HttpMethod.Post, app.Urls.Single() + "/internal/otel/v1/metrics") { Content = new ByteArrayContent(bytes) }; message.Headers.Add("X-Agent-Token", token); return message; }
            using var source = Report("source-token");
            var pending = http.SendAsync(source);
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var retirement = fence.WriteAsync("s", default);
            await Task.Delay(100); Assert.False(retirement.IsCompleted);
            finish.TrySetResult();
            using var accepted = await pending.WaitAsync(TimeSpan.FromSeconds(5)); Assert.Equal(HttpStatusCode.OK, accepted.StatusCode);
            await using (var authority = await retirement.WaitAsync(TimeSpan.FromSeconds(5)))
            {
                Assert.NotNull(authority);
                await using var connection = new NpgsqlConnection(database.Configuration.GetConnectionString("Postgres")); await connection.OpenAsync();
                await using var rotate = new NpgsqlCommand("UPDATE sessions SET callback_token='target-token',execution_generation=2 WHERE id='s'", connection); await rotate.ExecuteNonQueryAsync();
            }
            using var late = Report("source-token"); using var rejected = await http.SendAsync(late);
            Assert.Equal(HttpStatusCode.Unauthorized, rejected.StatusCode);
            Assert.Equal(10, (await usage.GetAsync("alice", "s"))!.InputTokens);
            using var target = Report("target-token"); using var current = await http.SendAsync(target);
            Assert.Equal(HttpStatusCode.OK, current.StatusCode);
            Assert.Equal(20, (await usage.GetAsync("alice", "s"))!.InputTokens);
        }
        finally { finish.TrySetResult(); await app.StopAsync(); }
    }

    private sealed class LoopbackPod : IAgentPodIdentityResolver
    {
        public Task<bool> IsLiveSessionPodAsync(string sessionId, IPAddress address, CancellationToken ct = default)
            => Task.FromResult(sessionId == "s" && IPAddress.IsLoopback(address));
    }
}
