using AgentHub.Api.Models;
using AgentHub.Api.Persistence;
using AgentHub.Api.Services;

namespace AgentHub.Api.Files;

public interface IAgentCallbackAuthorizer
{
    Task<SessionRecord?> AuthorizeAsync(
        HttpRequest request,
        string sessionId,
        CancellationToken ct = default);
}

public sealed class AgentCallbackAuthorizer(ISessionStore sessions, IExecutionFence? fence = null) : IAgentCallbackAuthorizer
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
        if (session is null || !string.Equals(session.Id, sessionId, StringComparison.Ordinal)) return null;
        if (fence is null || !session.WorkspacePersistent) return session;
        var leaseKey = "agenthub.execution-fence:" + sessionId;
        if (!request.HttpContext.Items.ContainsKey(leaseKey))
        {
            var lease = await fence.ReadAsync(sessionId, ct);
            // Retire waits for whole callback handlers, not just their token lookup.
            request.HttpContext.Items[leaseKey] = lease;
            request.HttpContext.Response.RegisterForDisposeAsync(lease);
        }
        var current = await sessions.GetByCallbackTokenAsync(token!, ct);
        return current?.Id == sessionId ? current : null;
    }
}
