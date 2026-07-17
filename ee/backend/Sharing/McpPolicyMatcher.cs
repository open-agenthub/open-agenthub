namespace AgentHub.Api.Ee.Sharing;

public static class McpPolicyMatcher
{
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
