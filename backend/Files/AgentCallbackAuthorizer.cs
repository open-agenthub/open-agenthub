using AgentHub.Api.Models;
using AgentHub.Api.Persistence;

namespace AgentHub.Api.Files;

public interface IAgentCallbackAuthorizer
{
    Task<SessionRecord?> AuthorizeAsync(
        HttpRequest request,
        string sessionId,
        CancellationToken ct = default);
}

public sealed class AgentCallbackAuthorizer(ISessionStore sessions) : IAgentCallbackAuthorizer
{
    public async Task<SessionRecord?> AuthorizeAsync(
        HttpRequest request,
        string sessionId,
        CancellationToken ct = default)
    {
        if (!request.Headers.TryGetValue("X-Agent-Token", out var token) ||
            string.IsNullOrWhiteSpace(token))
        {
            return null;
        }

        var session = await sessions.GetByCallbackTokenAsync(token!, ct);
        return session is not null &&
            string.Equals(session.Id, sessionId, StringComparison.Ordinal)
                ? session
                : null;
    }
}
