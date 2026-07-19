using AgentHub.Api.Persistence;

namespace AgentHub.Api.Services;

public sealed record AgentSessionResourcePreparation(bool ShouldSpawn, bool HasGitCredentials);

/// <summary>
/// Ensures credential preflight completes before any session-scoped Kubernetes resource is prepared.
/// </summary>
public static class AgentSessionResourceOrchestrator
{
    public static async Task<AgentSessionResourcePreparation> PrepareAsync(
        SessionRecord record,
        PodBuildContext context,
        Func<string, CancellationToken, Task> failSession,
        Func<CancellationToken, Task<bool>> prepareResources,
        CancellationToken ct)
    {
        var diagnostic = AgentPodSpecFactory.MissingCredentialDiagnostic(record, context);
        if (diagnostic is not null)
        {
            await failSession(diagnostic, ct);
            return new AgentSessionResourcePreparation(false, false);
        }

        var hasGitCredentials = await prepareResources(ct);
        return new AgentSessionResourcePreparation(true, hasGitCredentials);
    }
}
