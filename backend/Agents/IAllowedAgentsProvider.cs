using AgentHub.Api.Models;

namespace AgentHub.Api.Agents;

/// <summary>
/// Resolves which <see cref="AgentKind"/> values may be used when creating or
/// starting sessions. Community Edition always allows every known agent; the
/// Enterprise Edition can optionally restrict to an admin-managed whitelist.
/// </summary>
public interface IAllowedAgentsProvider
{
    Task<IReadOnlyList<AgentKind>> GetAllowedAsync(CancellationToken ct = default);
    Task<bool> IsAllowedAsync(AgentKind agent, CancellationToken ct = default);
}
