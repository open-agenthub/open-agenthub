// -----------------------------------------------------------------------------
// Open AgentHub Enterprise Edition — Session sharing.
// Part of the Enterprise Edition; NOT covered by the AGPL-3.0 license of the
// open-core. Source-available under the Open AgentHub Enterprise License
// (see ee/LICENSE); a valid subscription is required for production use.
// -----------------------------------------------------------------------------
using System.Text.Json;
using AgentHub.Api.Models;

namespace AgentHub.Api.Ee.Sharing;

public static class McpPolicyMatcher
{
    public static bool IsBlocked(
        string toolName,
        IReadOnlyCollection<string> blockedServers,
        IReadOnlyCollection<string> blockedTools,
        AgentKind agent = AgentKind.Claude,
        JsonElement input = default)
    {
        ArgumentNullException.ThrowIfNull(toolName);
        ArgumentNullException.ThrowIfNull(blockedServers);
        ArgumentNullException.ThrowIfNull(blockedTools);

        // Compare the policy in the runtime's namespace: the CLI may replace punctuation
        // before invoking its hook. Colliding names are denied together rather than guessed.
        string Normalize(string name) => agent is AgentKind.Codex or AgentKind.OpenCode
            ? name.Replace('-', '_').Replace('.', '_') : name;
        var normalizedTool = Normalize(toolName);
        if (blockedTools.Any(name => Normalize(name) == normalizedTool))
            return true;

        foreach (var server in blockedServers)
        {
            if (string.IsNullOrEmpty(server))
                continue;

            // Unscoped discovery can contact every configured server. If the runtime
            // omits attribution, require a server before allowing this operation.
            if (IsResourceTool(toolName)
                && (input.ValueKind != JsonValueKind.Object
                    || !input.TryGetProperty("server", out var argument)
                    || argument.ValueKind != JsonValueKind.String
                    || string.IsNullOrEmpty(argument.GetString())
                    || Normalize(argument.GetString()!) == Normalize(server)))
                return true;

            var serverTool = $"mcp__{Normalize(server)}";
            if (string.Equals(normalizedTool, serverTool, StringComparison.Ordinal)
                || normalizedTool.StartsWith(serverTool + "__", StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    public static bool IsResourceTool(string toolName) => toolName is
        "read_mcp_resource" or "list_mcp_resources" or "list_mcp_resource_templates";
}

