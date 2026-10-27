using k8s.Models;

namespace AgentHub.Api.Services;

/// <summary>
/// The part of a scheduled session's pod template that depends on its project. Files and project
/// file access are resolved when they are read (the files copy, the project resolver); only the
/// names of the environment variables are fixed into the template, as <c>secretKeyRef</c>s into
/// the session's variable copy. A project move therefore replaces exactly that block.
/// Rejected: rebuilding the whole template. It would also re-resolve the image, resources, the
/// provider account and the presigned URLs, changing settings the owner did not ask to change, and
/// re-run credential preflight, whose failure marks the session failed (docs/session-organization.md).
/// </summary>
public static class ScheduledTemplateSecrets
{
    public const string NamesVariable = "AGENTHUB_SECRET_ENV";

    /// <summary>The agent container's variables with the secret block replaced by
    /// <paramref name="names"/>, in the position and form <see cref="AgentPodSpecFactory"/>
    /// writes it. Throws <see cref="InvalidOperationException"/> for a template without the block
    /// (created before session secrets existed), which cannot be updated in place.</summary>
    public static (int ContainerIndex, List<V1EnvVar> Env) Replace(V1PodSpec spec, string sessionId, IReadOnlyList<string> names)
    {
        var index = spec.Containers.ToList().FindIndex(c => c.Name == "agent");
        var env = index < 0 ? null : spec.Containers[index].Env?.ToList();
        var anchor = env?.FindIndex(e => e.Name == NamesVariable) ?? -1;
        if (env is null || anchor < 0)
            throw new InvalidOperationException("This schedule predates project secrets. Recreate it in the destination project.");
        var copy = SessionSecretDeliveryFactory.EnvSecretName(sessionId);
        env.RemoveAll(e => e.Name == NamesVariable || e.ValueFrom?.SecretKeyRef?.Name == copy);
        env.InsertRange(anchor, [new V1EnvVar { Name = NamesVariable, Value = string.Join(",", names) },
            .. SessionSecretDeliveryFactory.EnvVars(sessionId, names)]);
        return (index, env);
    }
}
