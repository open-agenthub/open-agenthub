using System.Text.Json;
using AgentHub.Api.Library;
using Xunit;

namespace AgentHub.Api.Tests;

public class McpConfigAssemblerTests
{
    private static McpServerRecord RawServer(
        string name,
        string config = "{\"type\":\"http\",\"url\":\"https://docs.example.test\"}") =>
        new()
        {
            Id = $"id-{name}",
            Owner = "alice",
            Name = name,
            Kind = "raw",
            ConfigJson = config
        };

    private static McpServerRecord ApiServer(string name, string id) =>
        new()
        {
            Id = id,
            Owner = "alice",
            Name = name,
            Kind = "api",
            ConfigJson = """{"specType":"openapi","specUrl":"https://api.example.test/openapi.json"}"""
        };

    [Fact]
    public void NothingConfigured_ReturnsNull()
    {
        Assert.Null(McpConfigAssembler.Merge(null, []));
        Assert.Null(McpConfigAssembler.Merge("", []));
        Assert.Null(McpConfigAssembler.Merge("{\"mcpServers\":{}}", []));
        Assert.Null(McpConfigAssembler.Merge("{}", []));
    }

    [Fact]
    public void RawServersOnly_BuildsMcpJson()
    {
        var json = McpConfigAssembler.Merge(null, [RawServer("docs")]);
        using var doc = JsonDocument.Parse(json!);
        var servers = doc.RootElement.GetProperty("mcpServers");
        Assert.Equal("https://docs.example.test", servers.GetProperty("docs").GetProperty("url").GetString());
    }

    [Fact]
    public void ApiServer_EmitsGatewayPlaceholderUrl()
    {
        var json = McpConfigAssembler.Merge(null, [ApiServer("petstore", "srv-42")]);
        using var doc = JsonDocument.Parse(json!);
        var entry = doc.RootElement.GetProperty("mcpServers").GetProperty("petstore");
        Assert.Equal("http", entry.GetProperty("type").GetString());
        Assert.Equal("https://mcp.invalid/srv-42", entry.GetProperty("url").GetString());
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
        var json = McpConfigAssembler.Merge(inline, [RawServer("docs"), RawServer("git")]);
        using var doc = JsonDocument.Parse(json!);
        var servers = doc.RootElement.GetProperty("mcpServers");
        Assert.Equal("my-own", servers.GetProperty("docs").GetProperty("command").GetString());
        Assert.True(servers.TryGetProperty("git", out _));
    }

    [Fact]
    public void InlineWinsOverApiServer_SameName()
    {
        var inline = "{\"mcpServers\":{\"petstore\":{\"command\":\"local\"}}}";
        var json = McpConfigAssembler.Merge(inline, [ApiServer("petstore", "srv-1")]);
        using var doc = JsonDocument.Parse(json!);
        var entry = doc.RootElement.GetProperty("mcpServers").GetProperty("petstore");
        Assert.Equal("local", entry.GetProperty("command").GetString());
        Assert.False(entry.TryGetProperty("url", out _));
    }

    [Fact]
    public void OtherTopLevelKeys_ArePreserved()
    {
        var inline = "{\"someExtension\":true}";
        var json = McpConfigAssembler.Merge(inline, [RawServer("docs")]);
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

    [Fact]
    public void InvalidRawConfig_Throws()
    {
        Assert.Throws<ArgumentException>(() =>
            McpConfigAssembler.Merge(null, [RawServer("bad", "not-json")]));
    }
}
