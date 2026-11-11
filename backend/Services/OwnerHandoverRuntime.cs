using System.Text.Json;
using AgentHub.Api.Admin;
using AgentHub.Api.Models;
using k8s;
using k8s.Models;

namespace AgentHub.Api.Services;

/// <summary>
/// The cluster half of an owner handover (docs/apikey-handover.md): which sessions are live, the
/// stored credentials, and scheduled sessions' CronJobs. All of these are named or labelled after
/// the owner, so none of them follows a change of the owner column by itself.
/// </summary>
public interface IOwnerHandoverRuntime
{
    /// <summary>Sessions of the owner with a pod that is not finished: running, starting or a scheduled run.</summary>
    Task<IReadOnlyList<string>> LiveSessionIdsAsync(string owner, CancellationToken ct = default);
    /// <summary>What <see cref="CopyCredentialsAsync"/> would do, without values.</summary>
    Task<IReadOnlyList<CredentialKindPreview>> CredentialPreviewAsync(string from, string to, CancellationToken ct = default);
    /// <summary>Merges the source's stored credentials into the target's secrets. Leaves the source's
    /// as they are, so a failure before the database switch hands nothing over that a rerun would
    /// not finish.</summary>
    Task CopyCredentialsAsync(string from, string to, CancellationToken ct = default);
    /// <summary>Removes the owner's stored-credential secrets (after a committed handover).</summary>
    Task DeleteCredentialsAsync(string owner, CancellationToken ct = default);
    /// <summary>Suspends (or, after a failed switch, restores) the schedules of these sessions.</summary>
    Task SuspendSchedulesAsync(IReadOnlyCollection<string> sessionIds, bool suspend, CancellationToken ct = default);
    /// <summary>Recreates a scheduled session's CronJob for its (new) owner.</summary>
    Task RebuildScheduleAsync(string owner, string sessionId, CancellationToken ct = default);
}

public sealed partial class KubernetesSessionService : IOwnerHandoverRuntime
{
    /// <summary>The schedule's own <c>spec.suspend</c> from before a handover suspended it.</summary>
    private const string HandoverSuspendAnnotation = "agenthub.dev/handover-suspend";

    private static readonly AgentKind[] ProviderAgents =
        [AgentKind.Claude, AgentKind.Codex, AgentKind.Cursor, AgentKind.OpenClaw, AgentKind.OpenCode];

    public async Task<IReadOnlyList<string>> LiveSessionIdsAsync(string owner, CancellationToken ct = default)
    {
        var pods = await _k8s.CoreV1.ListNamespacedPodAsync(_opts.Namespace,
            labelSelector: $"{OwnerLabel}={Sanitize(owner)},{ComponentLabel}=agent", cancellationToken: ct);
        // A terminating pod still uploads its state to the old owner's prefix, so it counts as live.
        return pods.Items
            .Where(p => p.Status?.Phase is not ("Succeeded" or "Failed") || p.Metadata.DeletionTimestamp is not null)
            .Select(p => p.Metadata.Labels?.TryGetValue(SessionLabel, out var id) == true ? id : null)
            .OfType<string>().Distinct().Order(StringComparer.Ordinal).ToList();
    }

    public async Task<IReadOnlyList<CredentialKindPreview>> CredentialPreviewAsync(string from, string to, CancellationToken ct = default)
    {
        var kinds = new List<CredentialKindPreview>();
        foreach (var (merge, _, _) in await MergesAsync(from, to, ct)) kinds.AddRange(merge.Kinds);
        return OwnerHandoverCredentials.Sum(kinds);
    }

    public async Task CopyCredentialsAsync(string from, string to, CancellationToken ct = default)
    {
        // A concurrent write to the target's secret (it saves a login meanwhile) makes the replace
        // fail on the resourceVersion; reading and merging again keeps both.
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                foreach (var (merge, name, existing) in await MergesAsync(from, to, ct))
                {
                    if (merge.Kinds.Count == 0) continue;
                    var secret = new V1Secret
                    {
                        Metadata = new V1ObjectMeta
                        {
                            Name = name, NamespaceProperty = _opts.Namespace,
                            Labels = new Dictionary<string, string> { [OwnerLabel] = Sanitize(to) },
                            ResourceVersion = existing?.Metadata.ResourceVersion,
                        },
                        Type = "Opaque", Data = merge.Data,
                    };
                    if (existing is null) await _k8s.CoreV1.CreateNamespacedSecretAsync(secret, _opts.Namespace, cancellationToken: ct);
                    else await _k8s.CoreV1.ReplaceNamespacedSecretAsync(secret, name, _opts.Namespace, cancellationToken: ct);
                }
                return;
            }
            catch (k8s.Autorest.HttpOperationException e) when (e.Response.StatusCode == System.Net.HttpStatusCode.Conflict && attempt < 5)
            {
                _log.LogInformation("A target credential secret changed during the handover copy; merging again");
            }
        }
    }

    public async Task DeleteCredentialsAsync(string owner, CancellationToken ct = default)
    {
        foreach (var name in CredentialSecretNames(owner).Select(n => n.Name))
            await DeleteSecretIfPresentAsync(name, ct);
    }

    public async Task SuspendSchedulesAsync(IReadOnlyCollection<string> sessionIds, bool suspend, CancellationToken ct = default)
    {
        foreach (var id in sessionIds)
        {
            V1CronJob cron;
            try { cron = await _k8s.BatchV1.ReadNamespacedCronJobAsync($"session-{id}", _opts.Namespace, cancellationToken: ct); }
            catch (k8s.Autorest.HttpOperationException e) when (e.Response.StatusCode == System.Net.HttpStatusCode.NotFound) { continue; }
            string? before = null;
            var marked = cron.Metadata.Annotations?.TryGetValue(HandoverSuspendAnnotation, out before) == true;
            object patch;
            if (suspend)
            {
                // A rerun must not record the handover's own suspension as the schedule's state.
                if (marked) continue;
                patch = new
                {
                    metadata = new { resourceVersion = cron.Metadata.ResourceVersion,
                        annotations = new Dictionary<string, string> { [HandoverSuspendAnnotation] = cron.Spec.Suspend == true ? "true" : "false" } },
                    spec = new { suspend = true },
                };
            }
            else
            {
                if (!marked) continue;
                patch = new
                {
                    metadata = new { annotations = new Dictionary<string, string?> { [HandoverSuspendAnnotation] = null } },
                    spec = new { suspend = before == "true" },
                };
            }
            await _k8s.BatchV1.PatchNamespacedCronJobAsync(new V1Patch(JsonSerializer.Serialize(patch), V1Patch.PatchType.MergePatch),
                cron.Metadata.Name, _opts.Namespace, cancellationToken: ct);
        }
    }

    public async Task RebuildScheduleAsync(string owner, string sessionId, CancellationToken ct = default)
    {
        var rec = await _store.GetAsync(owner, sessionId, ct) ?? throw new KeyNotFoundException($"Session {sessionId} not found.");
        if (rec.Mode != SessionMode.Scheduled) return;
        var name = $"session-{sessionId}";
        V1CronJob? cron = null;
        try { cron = await _k8s.BatchV1.ReadNamespacedCronJobAsync(name, _opts.Namespace, cancellationToken: ct); }
        catch (k8s.Autorest.HttpOperationException e) when (e.Response.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            // Deleted by an earlier attempt that failed before the new one was created.
            _log.LogDebug("No schedule to replace for session {Id}", sessionId);
        }
        // Already rebuilt by an earlier attempt: labelled for the owner and no longer marked.
        string? labelled = null, before = null;
        cron?.Metadata.Labels?.TryGetValue(OwnerLabel, out labelled);
        var marked = cron?.Metadata.Annotations?.TryGetValue(HandoverSuspendAnnotation, out before) == true;
        if (cron is not null && labelled == Sanitize(owner) && !marked) return;
        var wasSuspended = marked ? before == "true" : cron?.Spec.Suspend == true;
        if (cron is not null)
        {
            // The template mounts the old owner's secrets by name and carries its labels; nothing short
            // of a new template points it at the new owner, and a CronJob template cannot be swapped
            // in place without racing a run the controller starts meanwhile (it is suspended here).
            await _k8s.BatchV1.DeleteNamespacedCronJobAsync(name, _opts.Namespace,
                propagationPolicy: "Background", cancellationToken: ct);
            for (var i = 0; i < 50; i++)
            {
                try { await _k8s.BatchV1.ReadNamespacedCronJobAsync(name, _opts.Namespace, cancellationToken: ct); }
                catch (k8s.Autorest.HttpOperationException e) when (e.Response.StatusCode == System.Net.HttpStatusCode.NotFound) { break; }
                await Task.Delay(200, ct);
            }
        }
        await SpawnAsync(owner, rec, RequestFromRecord(rec), resume: false, ct);
        if (wasSuspended)
            await _k8s.BatchV1.PatchNamespacedCronJobAsync(
                new V1Patch(JsonSerializer.Serialize(new { spec = new { suspend = true } }), V1Patch.PatchType.MergePatch),
                name, _opts.Namespace, cancellationToken: ct);
        _log.LogInformation("Rebuilt the schedule of session {Id} for its new owner", sessionId);
    }

    /// <summary>Every stored-credential secret of an owner, with the merge rule for its kind.</summary>
    private static IEnumerable<(string Name, Func<IDictionary<string, byte[]>?, IDictionary<string, byte[]>?, CredentialMerge> Merge)>
        CredentialSecretNames(string owner)
    {
        yield return (CredsSecretName(owner), OwnerHandoverCredentials.MergeGeneral);
        yield return (UserSecretsName(owner), OwnerHandoverCredentials.MergeUserSecrets);
        yield return (GitTokenStoreName(owner), OwnerHandoverCredentials.MergeGitOAuth);
        foreach (var agent in ProviderAgents)
            yield return (ProviderSecretName(owner, agent), (t, s) => OwnerHandoverCredentials.MergeProvider(t, s, agent));
    }

    private static string GitTokenStoreName(string owner) => KubernetesGitTokenStore.SecretName(GitAuthService.OwnerKey(owner));

    private async Task<List<(CredentialMerge Merge, string Name, V1Secret? Existing)>> MergesAsync(string from, string to, CancellationToken ct)
    {
        var result = new List<(CredentialMerge, string, V1Secret?)>();
        foreach (var ((sourceName, merge), (targetName, _)) in CredentialSecretNames(from).Zip(CredentialSecretNames(to)))
        {
            var source = await ReadSecretOrNullAsync(sourceName, ct);
            if (source?.Data is null || source.Data.Count == 0) continue;
            var target = await ReadSecretOrNullAsync(targetName, ct);
            result.Add((merge(target?.Data, source.Data), targetName, target));
        }
        return result;
    }
}
