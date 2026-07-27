using AgentHub.Api.Models;

namespace AgentHub.Api.Agents;

/// <summary>Shared allowlist check for session create / update / start / resume / duplicate.</summary>
public static class AllowedAgentsGuard
{
    public static async Task EnsureAgentAllowedAsync(
        IAllowedAgentsProvider provider, AgentKind agent, CancellationToken ct = default)
    {
        if (await provider.IsAllowedAsync(agent, ct))
            return;

        throw new AgentNotAllowedException(
            $"Agent '{agent}' is not allowed on this instance. " +
            "Ask an administrator to enable it, or choose a different agent.");
    }
}
