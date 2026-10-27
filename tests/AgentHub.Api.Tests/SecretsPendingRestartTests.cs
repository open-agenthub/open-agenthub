using AgentHub.Api.Services;
using k8s.Models;
using Xunit;

namespace AgentHub.Api.Tests;

/// <summary>
/// A running container keeps the variables it started with (#255): the pod records the
/// resourceVersion of the variable copy it started with, and a different one now means the agent
/// has stale variables until its next start.
/// </summary>
public class SecretsPendingRestartTests
{
    private static V1Pod Pod(string? version, string phase = "Running", bool deleting = false) => new()
    {
        Metadata = new V1ObjectMeta
        {
            Annotations = version is null ? null
                : new Dictionary<string, string> { [KubernetesSessionService.SecretEnvVersionAnnotation] = version },
            DeletionTimestamp = deleting ? DateTime.UtcNow : null
        },
        Status = new V1PodStatus { Phase = phase }
    };

    [Theory]
    [InlineData("41", "41", false)]
    [InlineData("41", "57", true)]
    public void ADifferentVersion_IsPending(string started, string now, bool expected) =>
        Assert.Equal(expected, KubernetesSessionService.SecretsPendingRestart(Pod(started), now));

    [Fact]
    public void NoLivePod_OrAPodStartedBeforeTheAnnotation_IsNotPending()
    {
        Assert.False(KubernetesSessionService.SecretsPendingRestart(null, "57"));
        Assert.False(KubernetesSessionService.SecretsPendingRestart(Pod("41", phase: "Succeeded"), "57"));
        Assert.False(KubernetesSessionService.SecretsPendingRestart(Pod("41", deleting: true), "57"));
        // Unknown is not "changed": nagging every session started before the upgrade would teach
        // the owner to ignore the note.
        Assert.False(KubernetesSessionService.SecretsPendingRestart(Pod(null), "57"));
    }

    [Fact]
    public void AWithdrawnCopy_LeavesTheRunningProcessWithWhatItStartedWith()
    {
        // #303: the copy is gone, the process is not; a deleted variable stays in it until a restart.
        Assert.True(KubernetesSessionService.SecretsPendingRestart(Pod("41"), null));
        Assert.True(KubernetesSessionService.SecretsWithdrawn(Pod("41"), null, copyExists: false));
        Assert.False(KubernetesSessionService.SecretsWithdrawn(Pod("41"), null, copyExists: true));
        // A pod from before recorded starts, or one that is not live, says nothing about copies.
        Assert.False(KubernetesSessionService.SecretsWithdrawn(Pod(null), null, copyExists: false));
        Assert.False(KubernetesSessionService.SecretsWithdrawn(Pod("41", phase: "Succeeded"), null, copyExists: false));
        Assert.False(KubernetesSessionService.SecretsWithdrawn(null, null, copyExists: false));
    }

    [Fact]
    public void APendingPod_CountsAsLive() =>
        Assert.True(KubernetesSessionService.SecretsPendingRestart(Pod("41", phase: "Pending"), "57"));
}
