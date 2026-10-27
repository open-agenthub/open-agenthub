using k8s.Models;

namespace AgentHub.Api.Services;

public static class TemporaryVolume
{
    // fsGroup makes the emptyDir root group-writable but does not add the sticky bit.
    // An owned subdirectory lets an unprivileged init container set the final mount mode.
    public static V1Container Prepare(string image, string pullPolicy) => new()
    {
        Name = "prepare-tmp",
        Image = image,
        ImagePullPolicy = pullPolicy,
        Command = ["/bin/sh", "-ec", "mkdir -p /volume/tmp && chmod 1777 /volume/tmp"],
        VolumeMounts = [new() { Name = "tmp", MountPath = "/volume" }],
        SecurityContext = new V1SecurityContext
        {
            RunAsNonRoot = true,
            RunAsUser = 1000,
            RunAsGroup = 1000,
            ReadOnlyRootFilesystem = true,
            AllowPrivilegeEscalation = false,
            Capabilities = new V1Capabilities { Drop = ["ALL"] }
        }
    };
}
