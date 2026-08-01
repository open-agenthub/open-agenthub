using System.Text.Json;
using AgentHub.Api.Library;
using AgentHub.Api.Models;
using AgentHub.Api.Persistence;
using AgentHub.Api.Services;
using Xunit;

namespace AgentHub.Api.Tests;

public class LibrarySessionIntegrationTests
{
    private static SessionRecord Source(string? mcpServerIdsJson, string? mcpConfigJson = null) => new()
    {
        Id = "source", Owner = "alice", Title = "Source",
        Mode = SessionMode.Interactive,
        AgentSessionId = "agent-session", CallbackToken = "token",
        McpConfigJson = mcpConfigJson,
        McpServerIdsJson = mcpServerIdsJson
    };

    private static (LibraryAccessService Access, InMemoryMcpServerStore Mcp) AccessFixture()
    {
        var mcp = new InMemoryMcpServerStore();
        var access = new LibraryAccessService(mcp, new FakeLibraryShareReader(), new FakeEnterpriseLicense(false));
        return (access, mcp);
    }

    /// <summary>
    /// Create-path seam: catalog-only (null/empty inline) stores resolved ids and
    /// produces effective .mcp.json containing the catalog server name — what spawn
    /// would write into the MCP secret.
    /// </summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public async Task CreatePath_CatalogOnly_StoresIdsAndAssemblesEffectiveConfig(string? inline)
    {
        var (access, mcp) = AccessFixture();
        var server = mcp.Add("alice", "docs", configJson: """{"type":"http","url":"https://docs.example.test"}""");

        // Mirrors create: strict resolve + assemble before any session Upsert.
        var (ids, effective) = await SessionMcpConfig.ResolveAndAssembleAsync(
            access, "alice", inline, [server.Id], strict: true);

        Assert.Equal([server.Id], ids);
        Assert.NotNull(effective);
        using var doc = JsonDocument.Parse(effective!);
        var entry = doc.RootElement.GetProperty("mcpServers").GetProperty("docs");
        Assert.Equal("https://docs.example.test", entry.GetProperty("url").GetString());

        // What create persists on the session row.
        var storedIdsJson = ids.Count == 0 ? null : JsonSerializer.Serialize(ids);
        Assert.Equal(JsonSerializer.Serialize(new[] { server.Id }), storedIdsJson);
    }

    [Fact]
    public async Task CreatePath_InvalidInlineShape_FailsBeforePersist()
    {
        var (access, mcp) = AccessFixture();
        var server = mcp.Add("alice", "docs");

        await Assert.ThrowsAsync<ArgumentException>(() =>
            SessionMcpConfig.ResolveAndAssembleAsync(
                access, "alice", "{\"mcpServers\":[]}", [server.Id], strict: true));
    }

    [Fact]
    public async Task CreatePath_BadCatalogConfig_FailsBeforePersist()
    {
        var (access, mcp) = AccessFixture();
        var server = mcp.Add("alice", "broken", configJson: "not-json");

        await Assert.ThrowsAsync<ArgumentException>(() =>
            SessionMcpConfig.ResolveAndAssembleAsync(
                access, "alice", null, [server.Id], strict: true));
    }

    [Fact]
    public void Duplication_CopiesLibraryServers_WhenMcpIsIncluded()
    {
        var copy = SessionDuplication.CopyableRequest(
            Source("[\"a\",\"b\"]", "{\"mcpServers\":{}}"),
            new DuplicateSessionRequest("Copy", null, IncludeMcp: true));
        Assert.Equal(["a", "b"], copy.McpServerIds);
        Assert.NotNull(copy.McpConfigJson);
    }

    [Fact]
    public void Duplication_DropsLibraryServers_WhenMcpIsExcluded()
    {
        var copy = SessionDuplication.CopyableRequest(
            Source("[\"a\"]", "{\"mcpServers\":{}}"),
            new DuplicateSessionRequest("Copy", null, IncludeMcp: false));
        Assert.Empty(copy.McpServerIds);
        Assert.Null(copy.McpConfigJson);
    }

    [Fact]
    public void Duplication_ExplicitServerList_OverridesTheCopy()
    {
        var withMcp = SessionDuplication.CopyableRequest(
            Source("[\"a\",\"b\"]"),
            new DuplicateSessionRequest("Copy", null, IncludeMcp: true, McpServerIds: ["b", "c"]));
        Assert.Equal(["b", "c"], withMcp.McpServerIds);

        // The explicit picker choice also applies when the inline config is excluded.
        var withoutMcp = SessionDuplication.CopyableRequest(
            Source("[\"a\"]", "{\"mcpServers\":{}}"),
            new DuplicateSessionRequest("Copy", null, IncludeMcp: false, McpServerIds: ["a"]));
        Assert.Equal(["a"], withoutMcp.McpServerIds);
        Assert.Null(withoutMcp.McpConfigJson);
    }

    [Fact]
    public void UpdateValidator_TreatsLibraryServersAsRuntimeField()
    {
        var scheduled = Source(null);
        scheduled.Mode = SessionMode.Scheduled;
        Assert.Throws<ArgumentException>(() => SessionUpdateValidator.Validate(
            scheduled, new UpdateSessionRequest { McpServerIds = ["a"] }));

        // Title-only updates stay allowed for scheduled sessions.
        SessionUpdateValidator.Validate(scheduled, new UpdateSessionRequest { Title = "New" });
    }
}
