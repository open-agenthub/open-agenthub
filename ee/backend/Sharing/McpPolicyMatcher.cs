// -----------------------------------------------------------------------------
// Open AgentHub Enterprise Edition — Session sharing.
// Part of the Enterprise Edition; NOT covered by the AGPL-3.0 license of the
// open-core. Source-available under the Open AgentHub Enterprise License
// (see ee/LICENSE); a valid subscription is required for production use.
// -----------------------------------------------------------------------------
namespace AgentHub.Api.Ee.Sharing;

public static class McpPolicyMatcher
{
    /// <summary>Whether the policy blocks anything at all; null is "no policy".</summary>
    public static bool Restricts(SessionMcpPolicy? policy) =>
        policy is not null && (policy.BlockedServers.Count > 0 || policy.BlockedTools.Count > 0);

    public static bool IsBlocked(
        string toolName,
        IReadOnlyCollection<string> blockedServers,
        IReadOnlyCollection<string> blockedTools)
    {
        ArgumentNullException.ThrowIfNull(toolName);
        ArgumentNullException.ThrowIfNull(blockedServers);
        ArgumentNullException.ThrowIfNull(blockedTools);

        if (blockedTools.Contains(toolName, StringComparer.Ordinal))
            return true;

        foreach (var server in blockedServers)
        {
            if (string.IsNullOrEmpty(server))
                continue;

            var serverTool = $"mcp__{server}";
            if (string.Equals(toolName, serverTool, StringComparison.Ordinal)
                || toolName.StartsWith(serverTool + "__", StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }
}
