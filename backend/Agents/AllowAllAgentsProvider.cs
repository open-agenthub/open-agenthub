using AgentHub.Api.Models;

namespace AgentHub.Api.Agents;

/// <summary>Community Edition default: every known <see cref="AgentKind"/> is allowed.</summary>
public sealed class AllowAllAgentsProvider : IAllowedAgentsProvider
{
    private static readonly IReadOnlyList<AgentKind> All =
        Enum.GetValues<AgentKind>().ToArray();

    public Task<IReadOnlyList<AgentKind>> GetAllowedAsync(CancellationToken ct = default)
        => Task.FromResult(All);

    public Task<bool> IsAllowedAsync(AgentKind agent, CancellationToken ct = default)
        => Task.FromResult(Enum.IsDefined(agent));
}
