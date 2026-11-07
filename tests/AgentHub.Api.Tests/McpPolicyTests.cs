using AgentHub.Api.Ee.Sharing;
using AgentHub.Api.Models;
using System.Text.Json;
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
    [Theory]
    [InlineData(AgentKind.Codex)]
    [InlineData(AgentKind.OpenCode)]
    public void RuntimeNames_UseTheSamePolicyNamespace(AgentKind agent)
    {
        Assert.True(McpPolicyMatcher.IsBlocked("mcp__probe_srv__get_data_v2", [],
            ["mcp__probe-srv__get-data.v2"], agent));
        Assert.True(McpPolicyMatcher.IsBlocked("mcp__probe_srv__allowed", ["probe-srv"], [], agent));
        Assert.False(McpPolicyMatcher.IsBlocked("mcp__probe_srv_extra__allowed", ["probe-srv"], [], agent));
        Assert.False(McpPolicyMatcher.IsBlocked("mcp__probe_srv__allowed", [],
            ["mcp__probe-srv__get-data.v2"], agent));
    }

    [Theory]
    [InlineData("read_mcp_resource")]
    [InlineData("list_mcp_resources")]
    [InlineData("list_mcp_resource_templates")]
    public void ResourceOperations_ApplyServerPolicy(string tool)
    {
        var blocked = JsonSerializer.SerializeToElement(new { server = "probe_srv" });
        var allowed = JsonSerializer.SerializeToElement(new { server = "allowed" });
        Assert.True(McpPolicyMatcher.IsBlocked(tool, ["probe-srv"], [], AgentKind.Codex));
        Assert.True(McpPolicyMatcher.IsBlocked(tool, ["probe-srv"], [], AgentKind.Codex, blocked));
        Assert.False(McpPolicyMatcher.IsBlocked(tool, ["probe-srv"], [], AgentKind.Codex, allowed));
    }
}
