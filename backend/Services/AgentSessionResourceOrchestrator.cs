using AgentHub.Api.Persistence;
using AgentHub.Api.Storage;

namespace AgentHub.Api.Services;

public sealed record AgentSessionResourcePreparation(bool ShouldSpawn, bool HasGitCredentials);
public sealed record AgentSessionArtifactUrls(
    string StatePutUrl,
    string StateGetUrl,
    string ScrollbackPutUrl,
    string TranscriptPutUrl);

/// <summary>
/// Ensures credential preflight completes before any session-scoped Kubernetes resource is prepared.
/// </summary>
public static class AgentSessionResourceOrchestrator
{
    public static string NativeStateKey(string ownerKey, SessionRecord record) =>
        record.ExecutionGeneration == 0 ? IArtifactStore.StateKey(ownerKey, record.Id, record.Agent)
            : IArtifactStore.ExecutionStateKey(ownerKey, record.Id, record.ExecutionGeneration, record.Agent);

    public static AgentSessionArtifactUrls PresignArtifactUrls(
        IArtifactStore artifacts, string ownerKey, SessionRecord record, bool resume, TimeSpan ttl)
    {
        var stateKey = NativeStateKey(ownerKey, record);
        return new AgentSessionArtifactUrls(
            artifacts.PresignPut(stateKey, ttl),
            resume ? artifacts.PresignGet(stateKey, ttl) : "",
            artifacts.PresignPut(record.ExecutionGeneration == 0 ? IArtifactStore.ScrollbackKey(ownerKey, record.Id) : IArtifactStore.ExecutionScrollbackKey(ownerKey, record.Id, record.ExecutionGeneration), ttl),
            artifacts.PresignPut(record.ExecutionGeneration == 0 ? IArtifactStore.TranscriptKey(ownerKey, record.Id) : IArtifactStore.ExecutionTranscriptKey(ownerKey, record.Id, record.ExecutionGeneration), ttl));
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
