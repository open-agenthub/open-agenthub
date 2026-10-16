using System.Security.Claims;
using ModelContextProtocol;

namespace AgentHub.Api.Mcp;

/// <summary>
/// The user a remote MCP tool call runs as. The access token carries "preferred_username", the
/// same claim the REST API keys ownership on. One helper for every tool class on the endpoint, so
/// a second class cannot resolve the owner differently from the first.
/// </summary>
public static class McpCallerIdentity
{
    public static string Owner(IHttpContextAccessor http)
    {
        var user = http.HttpContext?.User;
        var name = user?.FindFirstValue("preferred_username")
                   ?? user?.FindFirstValue(ClaimTypes.NameIdentifier)
                   ?? user?.Identity?.Name;
        // Reaching a tool without an identity would mean the endpoint was mapped without its
        // authorization policy; refuse rather than silently acting as somebody.
        return string.IsNullOrWhiteSpace(name)
            ? throw new McpException("unauthenticated")
            : name;
    }
}
