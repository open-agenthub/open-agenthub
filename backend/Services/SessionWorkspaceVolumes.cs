using System.Net;
using System.Security.Cryptography;
using System.Text;
using AgentHub.Api.Models;
using AgentHub.Api.Persistence;
using k8s;
using k8s.Models;

namespace AgentHub.Api.Services;

public sealed class SessionWorkspaceVolumes(Kubernetes cluster, ISessionWorkspaceStore store, string ns)
{
    public const string NonceLabel = "agenthub.dev/workspace";
    public static bool Supported(SessionRecord record) => record.Mode == SessionMode.Interactive
        && record.UiMode == SessionUiMode.Terminal && record.Agent is AgentKind.Claude or AgentKind.Codex
        && record.Image is null && !record.RunAsRoot;
    public static string Fingerprint(string repositories) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(repositories))).ToLowerInvariant();

    public async Task CreateAsync(SessionRecord record, string size, string? storageClass, CancellationToken ct)
    {
        if (SessionResources.Parse(size) is not { } bytes || bytes < 1024 * 1024)
            throw new ArgumentException("Workspace size must be a positive storage quantity of at least 1Mi.");
        var workspace = new SessionWorkspace(record.Id, "workspace-" + record.Id, null, size,
            string.IsNullOrWhiteSpace(storageClass) ? null : storageClass.Trim(), Guid.NewGuid().ToString("N"), record.ReposJson ?? "[]");
        // Persist admission before the Kubernetes call, so an ambiguous create can be reconciled.
        await store.AddAsync(record.Owner, workspace, ct);
        await EnsureAsync(record, ct);
    }

    public async Task<SessionWorkspace?> EnsureAsync(SessionRecord record, CancellationToken ct)
    {
        var workspace = await store.GetAsync(record.Owner, record.Id, ct);
        if (workspace is null) return null;
        if (workspace.Repositories != (record.ReposJson ?? "[]"))
            throw new InvalidOperationException("Repository layout changes are unavailable for a persistent workspace.");
        V1PersistentVolumeClaim? claim = null;
        try { claim = await cluster.CoreV1.ReadNamespacedPersistentVolumeClaimAsync(workspace.ClaimName, ns, cancellationToken: ct); }
        catch (k8s.Autorest.HttpOperationException error) when (error.Response.StatusCode == HttpStatusCode.NotFound) { }
        if (claim is null)
        {
            if (workspace.ClaimUid is not null)
                throw new InvalidOperationException("The recorded workspace claim is missing. Restore it before restarting the session.");
            claim = await cluster.CoreV1.CreateNamespacedPersistentVolumeClaimAsync(new V1PersistentVolumeClaim
            {
                Metadata = new V1ObjectMeta { Name = workspace.ClaimName, Labels = new Dictionary<string,string>
                {
                    ["agenthub.dev/owner"] = "u-" + Fingerprint(record.Owner)[..16],
                    ["agenthub.dev/session"] = record.Id, ["agenthub.dev/component"] = "workspace",
                    [NonceLabel] = workspace.Nonce
                } },
                Spec = new V1PersistentVolumeClaimSpec
                {
                    AccessModes = ["ReadWriteOnce"], VolumeMode = "Filesystem", StorageClassName = workspace.StorageClass,
                    Resources = new V1VolumeResourceRequirements { Requests = new Dictionary<string,ResourceQuantity> { ["storage"] = new(workspace.Size) } }
                }
            }, ns, cancellationToken: ct);
        }
        Verify(workspace, claim);
        if (claim.Metadata.DeletionTimestamp is not null) throw new InvalidOperationException("Workspace claim is terminating.");
        await store.BindAsync(record.Owner, record.Id, claim.Metadata.Uid, ct);
        return workspace with { ClaimUid = claim.Metadata.Uid };
    }

    public static void Verify(SessionWorkspace workspace, V1PersistentVolumeClaim claim)
    {
        bool Label(string key, string expected) => claim.Metadata.Labels is { } labels
            && labels.TryGetValue(key, out var value) && value == expected;
        if (claim.Metadata.Name != workspace.ClaimName || string.IsNullOrEmpty(claim.Metadata.Uid)
            || (workspace.ClaimUid is not null && claim.Metadata.Uid != workspace.ClaimUid)
            || !Label(NonceLabel, workspace.Nonce)
            || !Label("agenthub.dev/session", workspace.SessionId)
            || !Label("agenthub.dev/component", "workspace"))
            throw new InvalidOperationException("Workspace claim identity does not match this session.");
    }

    public async Task DeleteAsync(SessionRecord record, CancellationToken ct)
    {
        var workspace = await store.GetAsync(record.Owner, record.Id, ct);
        if (workspace is null) return;
        try
        {
            var claim = await cluster.CoreV1.ReadNamespacedPersistentVolumeClaimAsync(workspace.ClaimName, ns, cancellationToken: ct);
            Verify(workspace, claim);
            // A UID precondition closes replacement between the identity check and delete.
            await cluster.CoreV1.DeleteNamespacedPersistentVolumeClaimAsync(workspace.ClaimName, ns,
                body: new V1DeleteOptions { Preconditions = new V1Preconditions { Uid = claim.Metadata.Uid } }, cancellationToken: ct);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromMinutes(2));
            while (true)
            {
                await cluster.CoreV1.ReadNamespacedPersistentVolumeClaimAsync(workspace.ClaimName, ns, cancellationToken: timeout.Token);
                await Task.Delay(500, timeout.Token);
            }
        }
        catch (k8s.Autorest.HttpOperationException error) when (error.Response.StatusCode == HttpStatusCode.NotFound) { }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        { throw new TimeoutException("Workspace claim is still terminating; retry session deletion."); }
    }

    public static string BootstrapGuard => """
        set -e
        marker=/workspace/.agenthub-workspace
        if [ -L "$marker" ]; then echo "Workspace checkpoint is invalid." >&2; exit 1; fi
        if [ -f "$marker" ]; then
          if [ "$(cat "$marker")" = "$WORKSPACE_FINGERPRINT" ]; then echo "Reusing persistent workspace."; exit 0; fi
          echo "Workspace repository layout does not match its checkpoint." >&2; exit 1
        fi
        # No process may erase an existing checkout to repair an unconfirmed bootstrap.
        if [ -n "$(find /workspace -mindepth 1 -maxdepth 1 ! -name lost+found -print -quit)" ]; then
          echo "Workspace bootstrap is incomplete; existing files were preserved." >&2; exit 1
        fi
        """;
    public static string BootstrapComplete => """
        printf '%s\n' "$WORKSPACE_FINGERPRINT" > /workspace/.agenthub-workspace.tmp
        mv /workspace/.agenthub-workspace.tmp /workspace/.agenthub-workspace
        """;
}
