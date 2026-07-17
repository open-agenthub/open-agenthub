using AgentHub.Api.Ee.Sharing;
using Xunit;

namespace AgentHub.Api.Tests;

public class McpPolicyTests
{
    [Theory]
    [InlineData("mcp__slack", true)]
    [InlineData("mcp__slack__search", true)]
    [InlineData("mcp__slack_extra__search", false)]
    [InlineData("mcp__github__search", false)]
    [InlineData("slack", false)]
    public void BlockedServer_MatchesOnlyClaudeServerBoundary(string tool, bool expected)
        => Assert.Equal(expected, McpPolicyMatcher.IsBlocked(tool, ["slack"], []));

    [Theory]
    [InlineData("mcp__github__search", true)]
    [InlineData("mcp__github__search_more", false)]
    [InlineData("mcp__github__Search", false)]
    [InlineData("MCP__github__search", false)]
    public void BlockedTool_UsesOrdinalFullNameEquality(string tool, bool expected)
        => Assert.Equal(expected, McpPolicyMatcher.IsBlocked(
            tool,
            [],
            ["mcp__github__search"]));

    [Fact]
    public void EmptyPolicy_DoesNotBlock()
        => Assert.False(McpPolicyMatcher.IsBlocked("mcp__github__search", [], []));
}
