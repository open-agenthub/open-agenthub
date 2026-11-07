using System.Net;
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
    private sealed class LoopbackPod : IAgentPodIdentityResolver
    {
        public Task<bool> IsLiveSessionPodAsync(string sessionId, IPAddress address, CancellationToken ct = default)
            => Task.FromResult(sessionId == "s" && IPAddress.IsLoopback(address));
    }
}
