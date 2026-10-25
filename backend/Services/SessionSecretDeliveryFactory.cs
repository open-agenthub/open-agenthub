using AgentHub.Api.Models;
using k8s.Models;

namespace AgentHub.Api.Services;

/// <summary>
/// The two per-session secrets a session's selection is delivered through
/// (docs/session-secrets.md), and the pod spec parts that consume them.
///
/// Files and variables are kept apart because they are consumed differently. The file secret is
/// mounted whole, without <c>items</c> or <c>subPath</c>, which is what lets the kubelet bring a
/// changed, added or removed file into a running pod. The variable secret is referenced key by
/// key from the container's <c>env</c>; mounting it too would have put every variable on disk
/// a second time.
/// </summary>
public static class SessionSecretDeliveryFactory
{
    public const string FilesComponent = "session-secret-files";
    public const string EnvComponent = "session-secret-env";
    /// <summary>Under the agent's home, where the issue asked for it, but outside every state
    /// directory the archive packs (docs/state-archive.md), so a value is never uploaded with it.</summary>
    public const string DirectoryName = ".secrets";

    public static string FilesSecretName(string sessionId) => $"sessionsecrets-{sessionId}";
    public static string EnvSecretName(string sessionId) => $"sessionenv-{sessionId}";

    /// <summary>The two copies and the variable names the pod spec references.</summary>
    /// <param name="Withheld">How many selected secrets were left out because another selected one
    /// lands in the same place.</param>
    public sealed record Delivery(V1Secret Files, V1Secret Env, IReadOnlyList<string> EnvNames, int Withheld = 0);

    /// <summary>The selected entries split by delivery, keyed by the name the session sees.</summary>
    public static Delivery Build(string sessionId, string @namespace, IDictionary<string, string> labels,
        IDictionary<string, byte[]>? store, IReadOnlyList<string> effectiveIds)
    {
        var files = new Dictionary<string, byte[]>();
        var env = new Dictionary<string, byte[]>();
        var selected = SessionSecretStore.Read(store).Where(e => effectiveIds.Contains(e.Id)).ToList();
        // Every write path refuses a set where two secrets land in one place (SessionSecretConflicts),
        // so this only meets one that two concurrent edits produced. Neither is delivered then: with
        // one dictionary key the later entry would silently win, and an agent working with a value
        // the owner did not mean is worse than one that finds the variable missing.
        var clashing = selected.GroupBy(e => (e.Delivery, e.Name)).Where(g => g.Count() > 1)
            .SelectMany(g => g).Select(e => e.Id).ToHashSet();
        foreach (var entry in selected.Where(e => !clashing.Contains(e.Id)))
        {
            var value = SessionSecretStore.Value(store, entry.Id);
            if (value is null) continue;
            (entry.Delivery == SessionSecretDelivery.Env ? env : files)[entry.Name] = value;
        }
        return new Delivery(
            Secret(FilesSecretName(sessionId), @namespace, labels, FilesComponent, files),
            Secret(EnvSecretName(sessionId), @namespace, labels, EnvComponent, env),
            env.Keys.Order(StringComparer.Ordinal).ToList(), clashing.Count);
    }

    /// <summary>
    /// One <c>env</c> entry per variable. Optional, so a variable removed from the secret between
    /// the spec being built and the container starting leaves the variable unset instead of the
    /// container stuck in CreateContainerConfigError.
    /// </summary>
    public static IEnumerable<V1EnvVar> EnvVars(string sessionId, IReadOnlyList<string> names) =>
        names.Select(name => new V1EnvVar
        {
            Name = name,
            ValueFrom = new V1EnvVarSource
            {
                SecretKeyRef = new V1SecretKeySelector { Name = EnvSecretName(sessionId), Key = name, Optional = true }
            }
        });

    private static V1Secret Secret(string name, string @namespace, IDictionary<string, string> labels,
        string component, Dictionary<string, byte[]> data) => new()
    {
        Metadata = new V1ObjectMeta
        {
            Name = name,
            NamespaceProperty = @namespace,
            Labels = new Dictionary<string, string>(labels) { ["agenthub.dev/component"] = component }
        },
        Type = "Opaque",
        Data = data
    };
}
