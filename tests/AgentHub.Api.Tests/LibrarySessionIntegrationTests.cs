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
