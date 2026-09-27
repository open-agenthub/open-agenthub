using System.Net;

namespace AgentHub.Api.Mcp;

/// <summary>
/// Which redirect URIs a dynamically registered MCP client may claim.
///
/// Registration is anonymous, so this is the guard that stops an attacker registering a client
/// pointing at a host they control: https is trusted because the destination is authenticated
/// by TLS, and plain http only on a loopback address, where the destination cannot be anywhere
/// but the user's own machine (RFC 8252).
/// </summary>
public static class McpRedirectUri
{
    public static bool IsAcceptable(string? value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri)) return false;
        if (uri.Scheme == Uri.UriSchemeHttps) return true;
        if (uri.Scheme != Uri.UriSchemeHttp) return false;

        // RFC 8252 prefers the literal loopback IP, because "localhost" can in principle be
        // repointed by DNS or a hosts entry. It is still accepted: real MCP clients register
        // http://localhost:<port>/... and rejecting it would break them for a rebinding attack
        // that already requires control of the user's own machine.
        if (string.Equals(uri.Host, "localhost", StringComparison.OrdinalIgnoreCase)) return true;
        return IPAddress.TryParse(uri.Host, out var ip) && IPAddress.IsLoopback(ip);
    }
}
