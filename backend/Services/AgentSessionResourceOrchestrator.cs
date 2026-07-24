using AgentHub.Api.Persistence;
using AgentHub.Api.Storage;

namespace AgentHub.Api.Services;

public sealed record AgentSessionResourcePreparation(bool ShouldSpawn, bool HasGitCredentials);
public sealed record AgentSessionArtifactUrls(
    string StatePutUrl,
    string StateGetUrl,
    string ScrollbackPutUrl);

/// <summary>
/// Ensures credential preflight completes before any session-scoped Kubernetes resource is prepared.
/// </summary>
public static class AgentSessionResourceOrchestrator
{
    public static AgentSessionArtifactUrls PresignArtifactUrls(
        IArtifactStore artifacts, string ownerKey, SessionRecord record, bool resume, TimeSpan ttl)
    {
        var stateKey = IArtifactStore.StateKey(ownerKey, record.Id, record.Agent);
        return new AgentSessionArtifactUrls(
            artifacts.PresignPut(stateKey, ttl),
            resume ? artifacts.PresignGet(stateKey, ttl) : "",
            artifacts.PresignPut(IArtifactStore.ScrollbackKey(ownerKey, record.Id), ttl));
    }

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
