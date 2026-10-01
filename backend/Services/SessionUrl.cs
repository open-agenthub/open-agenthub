namespace AgentHub.Api.Services;

/// <summary>
/// Builds the link a person opens to take a session over.
///
/// The origin comes from <c>FrontendOrigin</c> rather than from <c>Mcp:PublicUrl</c>, although both
/// name the same deployment: <c>Mcp:PublicUrl</c> is the OAuth issuer and the MCP resource
/// identifier, and it only exists when the remote MCP endpoint is switched on — an instance that
/// drives sessions over the REST API alone would get no URL at all. <c>FrontendOrigin</c> is the
/// origin of the web app the link has to open, is rendered by the chart for every install, and is
/// already what the Telegram, Signal and Slack notifiers build <c>/s/{id}</c> links from.
///
/// Absent configuration yields null instead of a guess from the request: the URL is handed to a
/// user, so a forwarded Host header must not decide where they are sent.
/// </summary>
public static class SessionUrl
{
    public static string? For(string? frontendOrigin, string sessionId)
    {
        var origin = frontendOrigin?.Trim().TrimEnd('/');
        if (string.IsNullOrEmpty(origin) || string.IsNullOrEmpty(sessionId)) return null;
        return $"{origin}/s/{Uri.EscapeDataString(sessionId)}";
    }
}
