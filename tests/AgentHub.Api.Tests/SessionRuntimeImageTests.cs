using System.Text.Json;
using AgentHub.Api.Models;
using AgentHub.Api.Persistence;
using AgentHub.Api.Services;
using Xunit;

namespace AgentHub.Api.Tests;

public class SessionRuntimeImageTests
{
    [Fact]
    public void PreferencePatchDistinguishesOmittedFalseAndReset()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        var omitted = JsonSerializer.Deserialize<UpdateSessionRequest>("{}", options)!;
        var disabled = JsonSerializer.Deserialize<UpdateSessionRequest>("{\"autoUpdateRuntimeImages\":false}", options)!;
        var reset = JsonSerializer.Deserialize<UpdateSessionRequest>("{\"autoUpdateRuntimeImages\":null}", options)!;
        Assert.False(omitted.AutoUpdateRuntimeImagesSpecified);
        Assert.True(disabled.AutoUpdateRuntimeImagesSpecified);
        Assert.False(disabled.AutoUpdateRuntimeImages);
        Assert.True(reset.AutoUpdateRuntimeImagesSpecified);
        Assert.Null(reset.AutoUpdateRuntimeImages);
    }

    [Theory]
    [InlineData(AgentKind.Claude, "chat")]
    [InlineData(AgentKind.Codex, "code")]
    [InlineData(AgentKind.Cursor, "editor")]
    [InlineData(AgentKind.OpenClaw, "claw")]
    [InlineData(AgentKind.OpenCode, "open")]
    public void UpdateUsesPodFactoryImageSelection(AgentKind agent, string expected)
        => Assert.Equal(expected, AgentPodSpecFactory.RuntimeImage(agent,
            new AgentRuntimeImages("chat", "code", "editor", "claw", "open", "IfNotPresent")));

    [PostgreSqlFact]
    public async Task PreferenceRoundTripsAndCanReturnToDeploymentDefault()
    {
        await using var database = await PostgresSharingDatabase.CreateAsync();
        var record = new SessionRecord { Id = "runtime-update", Owner = "alice", CallbackToken = "callback",
            AgentSessionId = "conversation", AutoUpdateRuntimeImages = true };
        await database.UpsertSessionAsync(record);
        Assert.True((await database.GetSessionAsync("alice", record.Id))!.AutoUpdateRuntimeImages);
        record.AutoUpdateRuntimeImages = false;
        await database.UpsertSessionAsync(record);
        Assert.False((await database.GetSessionAsync("alice", record.Id))!.AutoUpdateRuntimeImages);
        record.AutoUpdateRuntimeImages = null;
        await database.UpsertSessionAsync(record);
        Assert.Null((await database.GetSessionAsync("alice", record.Id))!.AutoUpdateRuntimeImages);
    }
}
