using System.Net;
using k8s;
using k8s.Models;

namespace AgentHub.Api.Services;

/// <summary>An owner's connected-provider secret as read; <see cref="ResourceVersion"/> is what a
/// conditional write must match, null when the secret does not exist yet.</summary>
public sealed record GitTokenSnapshot(string OwnerKey, IReadOnlyDictionary<string, byte[]> Data, string? ResourceVersion);

/// <summary>
/// Where connected-provider tokens live (the per-owner <c>gitauth-&lt;owner&gt;</c> secret), with
/// conditional writes. A refresh spends the provider's refresh token, so a write that silently
/// replaced a concurrent one would drop the only refresh token that still works.
/// </summary>
public interface IGitTokenStore
{
    Task<GitTokenSnapshot> ReadAsync(string ownerKey, CancellationToken ct);

    /// <summary>False when the secret changed (or appeared) since <paramref name="snapshot"/> was read.</summary>
    Task<bool> TryWriteAsync(GitTokenSnapshot snapshot, IReadOnlyDictionary<string, byte[]> data, CancellationToken ct);
}

public sealed class KubernetesGitTokenStore : IGitTokenStore
{
    private const string OwnerLabel = "agenthub.dev/owner";

    private readonly IKubernetes _k8s;
    private readonly string _ns;

    public KubernetesGitTokenStore(IConfiguration cfg)
        : this(new Kubernetes(KubernetesClientConfiguration.IsInCluster()
            ? KubernetesClientConfiguration.InClusterConfig()
            : KubernetesClientConfiguration.BuildConfigFromConfigFile()), cfg["AgentHub:Namespace"] ?? "agenthub")
    {
    }

    public KubernetesGitTokenStore(IKubernetes k8s, string ns)
    {
        _k8s = k8s;
        _ns = ns;
    }

    public static string SecretName(string ownerKey) => $"gitauth-{ownerKey}";

    public async Task<GitTokenSnapshot> ReadAsync(string ownerKey, CancellationToken ct)
    {
        try
        {
            var secret = await _k8s.CoreV1.ReadNamespacedSecretAsync(SecretName(ownerKey), _ns, cancellationToken: ct);
            return new GitTokenSnapshot(ownerKey,
                secret.Data is { } d ? new Dictionary<string, byte[]>(d) : new Dictionary<string, byte[]>(),
                secret.Metadata.ResourceVersion);
        }
        catch (k8s.Autorest.HttpOperationException e) when (e.Response.StatusCode == HttpStatusCode.NotFound)
        {
            return new GitTokenSnapshot(ownerKey, new Dictionary<string, byte[]>(), null);
        }
    }

    public async Task<bool> TryWriteAsync(GitTokenSnapshot snapshot, IReadOnlyDictionary<string, byte[]> data, CancellationToken ct)
    {
        var secret = new V1Secret
        {
            Metadata = new V1ObjectMeta
            {
                Name = SecretName(snapshot.OwnerKey), NamespaceProperty = _ns,
                Labels = new Dictionary<string, string> { [OwnerLabel] = snapshot.OwnerKey },
                ResourceVersion = snapshot.ResourceVersion
            },
            Type = "Opaque",
            Data = new Dictionary<string, byte[]>(data)
        };
        try
        {
            if (snapshot.ResourceVersion is null)
                await _k8s.CoreV1.CreateNamespacedSecretAsync(secret, _ns, cancellationToken: ct);
            else
                // With the resourceVersion set the apiserver refuses the replace (409) if anyone
                // wrote in between, instead of the last writer winning.
                await _k8s.CoreV1.ReplaceNamespacedSecretAsync(secret, secret.Metadata.Name, _ns, cancellationToken: ct);
            return true;
        }
        catch (k8s.Autorest.HttpOperationException e) when (e.Response.StatusCode == HttpStatusCode.Conflict)
        {
            return false;
        }
    }
}
