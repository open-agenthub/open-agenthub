// -----------------------------------------------------------------------------
// Open AgentHub Enterprise Edition — Session sharing.
// Part of the Enterprise Edition; NOT covered by the AGPL-3.0 license of the
// open-core. Source-available under the Open AgentHub Enterprise License
// (see ee/LICENSE); a valid subscription is required for production use.
// -----------------------------------------------------------------------------
using System.Text.RegularExpressions;
using AgentHub.Api.Models;

namespace AgentHub.Api.Ee.Sharing;

public static partial class McpPolicyMatcher
{
    /// <summary>Whether the policy blocks anything at all; null is "no policy".</summary>
    public static bool Restricts(SessionMcpPolicy? policy) =>
        policy is not null && (policy.BlockedServers.Count > 0 || policy.BlockedTools.Count > 0);

    /// <summary>
    /// Whether a tool call, named as <paramref name="agent"/>'s runtime names it, is blocked by
    /// names as the owner typed them: configured server names, and tool names in the
    /// <c>mcp__server__tool</c> form. Both sides go through <see cref="NormalizeMcpName"/>, because
    /// a runtime does not hand the hook the configured spelling: Claude reports the server
    /// "ops.tools" as <c>mcp__ops_tools__…</c>.
    /// </summary>
    public static bool IsBlocked(
        AgentKind agent,
        string toolName,
        IReadOnlyCollection<string> blockedServers,
        IReadOnlyCollection<string> blockedTools)
    {
        ArgumentNullException.ThrowIfNull(toolName);
        ArgumentNullException.ThrowIfNull(blockedServers);
        ArgumentNullException.ThrowIfNull(blockedTools);

        var tool = NormalizeMcpName(agent, toolName);
        if (blockedTools.Any(blocked => string.Equals(NormalizeMcpName(agent, blocked), tool, StringComparison.Ordinal)))
            return true;

        foreach (var server in blockedServers)
        {
            if (string.IsNullOrEmpty(server))
                continue;

            var serverTool = $"mcp__{NormalizeMcpName(agent, server)}";
            if (string.Equals(tool, serverTool, StringComparison.Ordinal)
                || tool.StartsWith(serverTool + "__", StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// A server name, or a whole <c>mcp__server__tool</c> name, spelled the way
    /// <paramref name="agent"/>'s runtime spells it in the tool names it reports. Idempotent, so
    /// a name the runtime already normalised passes unchanged.
    /// Claude Code (2.1.318, verified with the real CLI) replaces every character outside
    /// [A-Za-z0-9_-] with "_" and keeps case. Other runtimes are passed through verbatim until
    /// their own rule is verified and added here.
    /// </summary>
    public static string NormalizeMcpName(AgentKind agent, string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        return agent switch
        {
            AgentKind.Claude => ClaudeInvalidChars().Replace(name, "_"),
            _ => name
        };
    }

    [GeneratedRegex("[^A-Za-z0-9_-]")]
    private static partial Regex ClaudeInvalidChars();
}
