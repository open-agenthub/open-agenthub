using AgentHub.Api.Models;
using AgentHub.Api.Persistence;
using AgentHub.Api.Services;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AgentHub.Api.Tests;

public class ProviderSwitchMessageTests
{
    [PostgreSqlFact]
    public async Task CancellationKeepsInboxDurableUntilTheReleasedSourceAcknowledgesDelivery()
    {
        await using var database = await PostgresSharingDatabase.CreateAsync(pooling: false);
        await database.Sessions.UpsertAsync(new SessionRecord { Id = "s", Owner = "alice", Title = "Source",
            Agent = AgentKind.Claude, CallbackToken = "source-authority", AgentSessionId = "source-native" });
        var workspaces = new PostgresSessionWorkspaceStore(database.Configuration);
        await workspaces.InitializeAsync();
        await workspaces.AddAsync("alice", new("s", "workspace-s", null, "10Gi", null, "nonce", "[]"));
        var journal = new PostgresProviderSwitchStore(database.Configuration, new PostgresExecutionFence(database.Configuration));
        await journal.InitializeAsync();
        var request = new ProviderSwitchRequest(Guid.NewGuid(), "s", 0, AgentKind.Claude, "source-pod", "claim",
            new(AgentKind.Codex, AgentAuthMode.ApiKey, null, null, null), "Reviewed handover.");
        await journal.CreateAsync("alice", request);
        var messages = new PostgresSessionMessageStore(database.Configuration);
        await messages.InitializeAsync();
        var message = new SessionMessageRecord { Id = "queued", ToSessionId = "s", Owner = "alice", Body = "Continue after cancellation.", Priority = true };
        await messages.AddAsync(message);
        Assert.Single(await messages.ReadUndeliveredAsync("s", 10));
        Assert.Single(await messages.ReadUndeliveredAsync("s", 10));
        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        await using var app = builder.Build();
        app.Urls.Add("http://127.0.0.1:0");
        var pushes = 0;
        app.MapPost("/agenthub/messages", (HttpContext context) =>
        {
            Assert.Equal("source-authority", context.Request.Headers["X-Agent-Token"].ToString());
            pushes++;
            return Results.Json(new { delivered = "queued-for-mod" });
        });
        await app.StartAsync();
        try
        {
            var uri = new Uri(app.Urls.Single());
            var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["AgentHub:AgentPort"] = uri.Port.ToString() }).Build();
            using var http = new HttpClient();
            var delivery = new SessionMessageDelivery(TestPods.Live(http), database.Sessions, messages, config, NullLogger<SessionMessageDelivery>.Instance, journal);
            var target = new SessionInfo { Id = "s", Owner = "alice", Title = "Source", Mode = SessionMode.Interactive, Phase = "Running", PodIp = "127.0.0.1" };
            Assert.Equal(MessageDeliveryVia.Inbox, (await delivery.TryInjectReleasedAsync(target, message, null, default)).Via);
            Assert.Equal(0, pushes);
            await journal.MoveAsync("alice", "s", request.RequestId, "requested", new("cancelling"));
            Assert.Equal(MessageDeliveryVia.Inbox, (await delivery.TryInjectAsync(target, message, null, default)).Via);
            Assert.Single(await messages.ReadUndeliveredAsync("s", 10));
            Assert.Equal(MessageDeliveryVia.Mod, (await delivery.TryInjectReleasedAsync(target, message, null, default)).Via);
            Assert.Equal(1, pushes);
            Assert.Empty(await messages.ReadUndeliveredAsync("s", 10));
            Assert.True((await database.Sessions.GetAsync("alice", "s"))!.ProviderSwitchPending);
            await journal.MoveAsync("alice", "s", request.RequestId, "cancelling", new("cancelled"));
            Assert.False((await database.Sessions.GetAsync("alice", "s"))!.ProviderSwitchPending);
        }
        finally { await app.StopAsync(); }
    }
}
