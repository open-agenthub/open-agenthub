using System.Text.Json;
using AgentHub.Api.Library;
using Xunit;

namespace AgentHub.Api.Tests;

public class McpConfigAssemblerTests
{
    private static McpServerRecord Server(string name, string config = "{\"type\":\"http\",\"url\":\"https://docs.example.test\"}") =>
        new() { Id = $"id-{name}", Owner = "alice", Name = name, ConfigJson = config };

    [Fact]
    public void NothingConfigured_ReturnsNull()
    {
        Assert.Null(McpConfigAssembler.Merge(null, []));
        Assert.Null(McpConfigAssembler.Merge("", []));
        Assert.Null(McpConfigAssembler.Merge("{\"mcpServers\":{}}", []));
        Assert.Null(McpConfigAssembler.Merge("{}", []));
    }

    [Fact]
    public void ServersOnly_BuildsMcpJson()
    {
        var json = McpConfigAssembler.Merge(null, [Server("docs")]);
        using var doc = JsonDocument.Parse(json!);
        var servers = doc.RootElement.GetProperty("mcpServers");
        Assert.Equal("https://docs.example.test", servers.GetProperty("docs").GetProperty("url").GetString());
    }

    [Fact]
    public void InlineOnly_IsPreserved()
    {
        var inline = "{\"mcpServers\":{\"local\":{\"command\":\"npx\"}}}";
        var json = McpConfigAssembler.Merge(inline, []);
        using var doc = JsonDocument.Parse(json!);
        Assert.Equal("npx", doc.RootElement.GetProperty("mcpServers").GetProperty("local").GetProperty("command").GetString());
    }

    [Fact]
    public void InlineEntryWins_OnNameConflict()
    {
        var inline = "{\"mcpServers\":{\"docs\":{\"command\":\"my-own\"}}}";
        var json = McpConfigAssembler.Merge(inline, [Server("docs"), Server("git")]);
        using var doc = JsonDocument.Parse(json!);
        var servers = doc.RootElement.GetProperty("mcpServers");
        Assert.Equal("my-own", servers.GetProperty("docs").GetProperty("command").GetString());
        Assert.True(servers.TryGetProperty("git", out _));
    }

    [Fact]
    public void OtherTopLevelKeys_ArePreserved()
    {
        var inline = "{\"someExtension\":true}";
        var json = McpConfigAssembler.Merge(inline, [Server("docs")]);
        using var doc = JsonDocument.Parse(json!);
        Assert.True(doc.RootElement.GetProperty("someExtension").GetBoolean());
        Assert.True(doc.RootElement.GetProperty("mcpServers").TryGetProperty("docs", out _));
    }

    [Fact]
    public void OtherTopLevelKeys_WithoutServers_KeepConfig()
    {
        var json = McpConfigAssembler.Merge("{\"someExtension\":true}", []);
        Assert.NotNull(json);
        using var doc = JsonDocument.Parse(json!);
        Assert.True(doc.RootElement.GetProperty("someExtension").GetBoolean());
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("[1,2]")]
    [InlineData("{\"mcpServers\": []}")]
    public void InvalidInlineConfig_Throws(string inline)
    {
        Assert.Throws<ArgumentException>(() => McpConfigAssembler.Merge(inline, []));
    }
}
