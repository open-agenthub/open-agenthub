using System.Net;
using AgentHub.Api.Persistence;

namespace AgentHub.Api.Browser;

public interface IAgentPodIdentityResolver
{
    Task<bool> IsLiveSessionPodAsync(
        string sessionId, IPAddress sourceIp, CancellationToken ct = default);
}

public interface IBrowserRequestAuthorizer
{
    Task<SessionRecord?> AuthorizeAsync(string sessionId, string? token, IPAddress sourceIp,
        CancellationToken ct = default);
}

public sealed class BrowserRequestAuthorizer(
    ISessionStore sessions,
    IAgentPodIdentityResolver pods) : IBrowserRequestAuthorizer
{
    public async Task<SessionRecord?> AuthorizeAsync(string sessionId, string? token,
        IPAddress sourceIp, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(token)) return null;
        var session = await sessions.GetByCallbackTokenAsync(token, ct);
        if (session is null || !string.Equals(session.Id, sessionId, StringComparison.Ordinal))
            return null;
        return await pods.IsLiveSessionPodAsync(sessionId, sourceIp, ct) ? session : null;
    }
}

public static class IpAddressNormalization
{
    public static bool Equals(IPAddress left, IPAddress right)
    {
        if (left.IsIPv4MappedToIPv6) left = left.MapToIPv4();
        if (right.IsIPv4MappedToIPv6) right = right.MapToIPv4();
        return left.Equals(right);
    }
}
