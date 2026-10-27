using System.Formats.Tar;
using System.IO.Compression;
using System.Net;
using AgentHub.Api.Persistence;
using AgentHub.Api.Models;
using AgentHub.Api.Storage;
using k8s;

namespace AgentHub.Api.Services;

public sealed class ClaudeStateCleanup(IArtifactStore artifacts, ISessionStore sessions, ISessionExpiryLock locks,
    IConfiguration configuration, Kubernetes? cluster = null)
{
    private readonly Kubernetes _cluster = cluster ?? new Kubernetes(KubernetesClientConfiguration.IsInCluster()
        ? KubernetesClientConfiguration.InClusterConfig() : KubernetesClientConfiguration.BuildConfigFromConfigFile());
    private readonly string _namespace = configuration["AgentHub:Namespace"] ?? "agenthub-sessions";
    public sealed record Result(int Scanned, int Missing, int Clean, int Rewritten, int Skipped, int Failed);
    public const int MaxArchiveBytes = 256 * 1024 * 1024;

    public Task<Result> RunAsync(string owner, bool apply, CancellationToken ct)
        => RunCoreAsync(owner, apply, [AgentKind.Claude], false, ct);

    public Task<Result> RunProviderLoginsAsync(string owner, bool apply, CancellationToken ct)
        => RunCoreAsync(owner, apply, Enum.GetValues<AgentKind>(), true, ct);

    private async Task<Result> RunCoreAsync(string owner, bool apply, AgentKind[] agents, bool removeLogins, CancellationToken ct)
    {
        if (!artifacts.IsConfigured) throw new InvalidOperationException("Object storage is not configured.");
        var ownerPrefix = $"sessions/{KubernetesSessionService.OwnerKey(owner)}/";
        int scanned = 0, missing = 0, clean = 0, rewritten = 0, skipped = 0, failed = 0;
        await foreach (var prefix in artifacts.ListSessionPrefixesAsync(ct))
        {
            if (!prefix.StartsWith(ownerPrefix, StringComparison.Ordinal) || SessionStorageCleanup.SessionId(prefix) is null)
                continue;
            foreach (var agent in agents)
            {
                scanned++;
                try
                {
                    var id = SessionStorageCleanup.SessionId(prefix)!;
                    await using var held = await locks.TryAcquireAsync(id, ct);
                    if (held is null) { skipped++; continue; }
                    var session = await sessions.GetByIdAsync(id, ct);
                    if ((session is not null && session.Owner != owner) || await HasProducerAsync(id, ct))
                    { skipped++; continue; }
                    var key = IArtifactStore.StateKey(KubernetesSessionService.OwnerKey(owner), id, agent);
                    await using var input = await artifacts.OpenReadAsync(key, ct);
                    if (input is null) { missing++; continue; }
                    using var output = await RewriteAsync(input, agent, removeLogins, ct);
                    if (output is null) { clean++; continue; }
                    if (!apply) { rewritten++; continue; }
                    if (!await artifacts.TryPutStreamAsync(key, output, "application/gzip", output.Length, ct))
                        throw new IOException("Object storage is unavailable.");
                    rewritten++;
                }
                catch (Exception ex) when (ex is IOException or InvalidDataException or FormatException or Amazon.Runtime.AmazonServiceException or
                    Amazon.Runtime.AmazonClientException or HttpRequestException or k8s.Autorest.HttpOperationException)
                {
                    // Counts keep malformed tar names and storage error messages from exposing credentials.
                    failed++;
                }
            }
        }
        return new Result(scanned, missing, clean, rewritten, skipped, failed);
    }

    private async Task<bool> HasProducerAsync(string id, CancellationToken ct)
    {
        var name = $"session-{id}";
        // DELETE acceptance and Paused status do not mean the final SIGTERM upload finished.
        // The resume/delete lock and actual producer absence prevent overwriting that history.
        if ((await _cluster.CoreV1.ListNamespacedPodAsync(_namespace,
            labelSelector: $"agenthub.dev/session={id}", cancellationToken: ct)).Items.Count != 0) return true;
        if (await ExistsAsync(() => _cluster.CoreV1.ReadNamespacedPodAsync(name, _namespace, cancellationToken: ct))) return true;
        if (await ExistsAsync(() => _cluster.BatchV1.ReadNamespacedCronJobAsync(name, _namespace, cancellationToken: ct))) return true;
        return (await _cluster.BatchV1.ListNamespacedJobAsync(_namespace, cancellationToken: ct)).Items
            .Any(job => job.Metadata.OwnerReferences?.Any(reference => reference.Kind == "CronJob" && reference.Name == name) == true);
    }

    private static async Task<bool> ExistsAsync<T>(Func<Task<T>> read)
    {
        try { await read(); return true; }
        catch (k8s.Autorest.HttpOperationException ex) when (ex.Response.StatusCode == HttpStatusCode.NotFound)
        { return false; }
    }

    public static Task<MemoryStream?> RemoveBackupsAsync(Stream input, CancellationToken ct)
        => RewriteAsync(input, AgentKind.Claude, false, ct);

    public static Task<MemoryStream?> RemoveCredentialsAsync(Stream input, AgentKind agent, CancellationToken ct)
        => RewriteAsync(input, agent, true, ct);

    private static async Task<MemoryStream?> RewriteAsync(Stream input, AgentKind agent, bool removeLogins, CancellationToken ct)
    {
        using var raw = new MemoryStream();
        using (var gzip = new GZipStream(input, CompressionMode.Decompress, leaveOpen: true))
        {
            var buffer = new byte[81920];
            int count;
            while ((count = await gzip.ReadAsync(buffer, ct)) != 0)
            {
                if (raw.Length + count > MaxArchiveBytes) throw new InvalidDataException("Archive exceeds cleanup limit.");
                raw.Write(buffer, 0, count);
            }
        }
        raw.Position = 0;
        using var kept = new MemoryStream();
        using var reader = new TarReader(raw, leaveOpen: true);
        long start = 0;
        var removed = false;
        TarEntry? entry;
        while ((entry = reader.GetNextEntry()) is not null)
        {
            ct.ThrowIfCancellationRequested();
            entry.DataStream?.CopyTo(Stream.Null);
            var end = checked((raw.Position + 511) / 512 * 512);
            if (end > raw.Length) throw new InvalidDataException("Truncated tar entry.");
            var name = entry.Name;
            while (name.StartsWith("./", StringComparison.Ordinal)) name = name[2..];
            if ((agent == AgentKind.Claude && (name.TrimEnd('/') == ".claude/backups" || name.StartsWith(".claude/backups/", StringComparison.Ordinal))) ||
                (removeLogins && StateArchiveCredentials.Contains(agent, name)))
                removed = true;
            else
            {
                // Re-serializing TarEntry changes headers and extended metadata. Copy original blocks instead.
                kept.Write(raw.GetBuffer(), checked((int)start), checked((int)(end - start)));
            }
            start = end;
        }
        if (!removed) return null;
        kept.Write(raw.GetBuffer(), checked((int)start), checked((int)(raw.Length - start)));
        var output = new MemoryStream();
        using (var gzip = new GZipStream(output, CompressionLevel.Fastest, leaveOpen: true))
        {
            kept.Position = 0;
            await kept.CopyToAsync(gzip, ct);
        }
        output.Position = 0;
        return output;
    }
}
