using AgentHub.Api.Models;
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
        => Assert.Equal(expected, McpPolicyMatcher.IsBlocked(AgentKind.Codex, tool, ["slack"], []));

    [Theory]
    [InlineData("mcp__github__search", true)]
    [InlineData("mcp__github__search_more", false)]
    [InlineData("mcp__github__Search", false)]
    [InlineData("MCP__github__search", false)]
    public void BlockedTool_UsesOrdinalFullNameEquality(string tool, bool expected)
        => Assert.Equal(expected, McpPolicyMatcher.IsBlocked(
            AgentKind.Codex,
            tool,
            [],
            ["mcp__github__search"]));

    [Fact]
    public void EmptyPolicy_DoesNotBlock()
        => Assert.False(McpPolicyMatcher.IsBlocked(AgentKind.Codex, "mcp__github__search", [], []));

    // The names the real Claude Code 2.1.318 reported for servers "ops.tools" and "team-docs" and a
    // tool "get-data.v2", against the names an owner types into the sharing dialog.
    [Theory]
    [InlineData("mcp__ops_tools__search", true)]
    [InlineData("mcp__team-docs__search", true)]
    [InlineData("mcp__docs__get-data_v2", true)]
    [InlineData("mcp__docs__read", false)]
    [InlineData("mcp__ops_toolsx__search", false)]
    public void Claude_MatchesNamesAsTheCliSpellsThem(string tool, bool expected)
        => Assert.Equal(expected, McpPolicyMatcher.IsBlocked(
            AgentKind.Claude, tool, ["ops.tools", "team-docs"], ["mcp__docs__get-data.v2"]));

    [Theory]
    [InlineData("ops.tools", "ops_tools")]
    [InlineData("team-docs", "team-docs")]
    [InlineData("mcp__docs__get-data.v2", "mcp__docs__get-data_v2")]
    [InlineData("Docs Server", "Docs_Server")]
    [InlineData("mcp__ops_tools__search", "mcp__ops_tools__search")]
    public void NormalizeMcpName_ClaudeReplacesEverythingOutsideItsToolNameAlphabet(string name, string expected)
        => Assert.Equal(expected, McpPolicyMatcher.NormalizeMcpName(AgentKind.Claude, name));

    [Fact]
    public void NormalizeMcpName_CodexUsesTheUnderscoreAlphabet()
        => Assert.Equal("mcp__docs__get_data_v2", McpPolicyMatcher.NormalizeMcpName(AgentKind.Codex, "mcp__docs__get-data.v2"));

    // ReadMcpResourceTool and its siblings name the server in an argument; the decision is the
    // server-level block, and a call without a server would list every server's resources.
    [Theory]
    [InlineData("ReadMcpResourceTool", "ops.tools", true)]
    [InlineData("ReadMcpResourceTool", "ops_tools", true)]
    [InlineData("ReadMcpResourceDirTool", "ops.tools", true)]
    [InlineData("ListMcpResourcesTool", "ops.tools", true)]
    [InlineData("ListMcpResourcesTool", null, true)]
    [InlineData("ReadMcpResourceTool", "docs", false)]
    [InlineData("mcp__docs__search", null, true)]
    [InlineData("mcp__docs__read", null, false)]
    public void Claude_ResourceToolsAreDecidedByTheirServer(string tool, string? server, bool expected)
        => Assert.Equal(expected, McpPolicyMatcher.IsBlocked(AgentKind.Claude, tool, server,
            new SessionMcpPolicy(["ops.tools"], ["mcp__docs__search"], DateTime.UtcNow)));

    [Fact]
    public void ResourceTools_AreNotBlockedByToolLevelBlocksAlone()
        => Assert.False(McpPolicyMatcher.IsBlocked(AgentKind.Claude, "ListMcpResourcesTool", null,
            new SessionMcpPolicy([], ["mcp__docs__search"], DateTime.UtcNow)));

    [Fact]
    public void IsResourceTool_KnowsOnlyVerifiedNames()
    {
        Assert.True(McpPolicyMatcher.IsResourceTool(AgentKind.Claude, "ReadMcpResourceTool"));
        Assert.False(McpPolicyMatcher.IsResourceTool(AgentKind.Claude, "read_mcp_resource"));
        Assert.False(McpPolicyMatcher.IsResourceTool(AgentKind.Codex, "ReadMcpResourceTool"));
    }
    [Theory]
    [InlineData(AgentKind.Codex, "mcp__probe_srv__get_data_v2", "mcp__probe_srv__allowed")]
    [InlineData(AgentKind.OpenCode, "mcp__probe-srv__get-data_v2", "mcp__probe-srv__allowed")]
    public void RuntimeNames_UseTheSamePolicyNamespace(AgentKind agent, string blockedTool, string allowedTool)
    {
        Assert.True(McpPolicyMatcher.IsBlocked(agent, blockedTool, [], ["mcp__probe-srv__get-data.v2"]));
        Assert.True(McpPolicyMatcher.IsBlocked(agent, allowedTool, ["probe-srv"], []));
        Assert.False(McpPolicyMatcher.IsBlocked(agent, allowedTool, [], ["mcp__probe-srv__get-data.v2"]));
        var name = McpPolicyMatcher.NormalizeMcpName(agent, "mcp__probe-srv__get-data.v2");
        Assert.Equal(name, McpPolicyMatcher.NormalizeMcpName(agent, name));
    }

    [Theory]
    [InlineData(AgentKind.Codex)]
    [InlineData(AgentKind.OpenCode)]
    public void RuntimeResourceOperations_ApplyServerPolicy(AgentKind agent)
    {
        var policy = new SessionMcpPolicy(["probe-srv"], [], DateTime.UtcNow);
        foreach (var tool in new[] { "read_mcp_resource", "list_mcp_resources", "list_mcp_resource_templates" })
        {
            Assert.True(McpPolicyMatcher.IsBlocked(agent, tool, "probe-srv", policy));
            Assert.True(McpPolicyMatcher.IsBlocked(agent, tool, null, policy));
            Assert.False(McpPolicyMatcher.IsBlocked(agent, tool, "allowed", policy));
        }
    }
}
