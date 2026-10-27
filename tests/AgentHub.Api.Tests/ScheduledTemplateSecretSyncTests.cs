using AgentHub.Api.Models;
using AgentHub.Api.Services;
using Xunit;

namespace AgentHub.Api.Tests;

/// <summary>
/// A scheduled session's template follows changes of its project's secret selection, of a secret's
/// name or delivery, and of its grants (#301): the next run's variables are the ones the session's
/// variable copy holds, or the session says it is behind.
/// </summary>
public class ScheduledTemplateSecretSyncTests
{
    private static async Task<(ScheduleClusterRig Rig, string Old, string New)> Started()
    {
        var rig = await ScheduleClusterRig.Start();
        rig.SeedCopies("OLD_NAME");
        return (rig, rig.Project("source").SecretIds![0], rig.Project("target").SecretIds![0]);
    }

    [Fact]
    public async Task AChangedProjectSelection_ReachesTheNextRun()
    {
        var (rig, old, neu) = await Started();
        await using var _ = rig;
        rig.SetProject(rig.Project("source") with { SecretIds = [old, neu] });

        await rig.Service.SyncSessionSecretsAsync("owner");

        Assert.Equal(["NEW_NAME", "OLD_NAME"], rig.TemplateEnvNames.Where(n => n.EndsWith("_NAME")).Order());
        Assert.False((await rig.Service.GetSessionAsync("owner", "sid"))!.ScheduleSecretsOutdated);

        rig.SetProject(rig.Project("source") with { SecretIds = [neu] });
        await rig.Service.SyncSessionSecretsAsync("owner");
        Assert.Contains("NEW_NAME", rig.TemplateEnvNames);
        Assert.DoesNotContain("OLD_NAME", rig.TemplateEnvNames);
        Assert.Equal("new-value", rig.EnvCopy["NEW_NAME"]);
    }

    [Fact]
    public async Task ARenamedOrRemovedSecret_IsNotLeftBehindInTheTemplate()
    {
        var (rig, old, _) = await Started();
        await using var __ = rig;
        await rig.Service.UpsertSessionSecretAsync("owner", new("RENAMED_NAME", "env", "old-value", null, old));
        Assert.Contains("RENAMED_NAME", rig.TemplateEnvNames);
        Assert.DoesNotContain("OLD_NAME", rig.TemplateEnvNames);

        // Delivered as a file now: it leaves the variables, and a file reaches the run through the copy.
        await rig.Service.UpsertSessionSecretAsync("owner", new("RENAMED_NAME", "file", "old-value", null, old));
        Assert.DoesNotContain("RENAMED_NAME", rig.TemplateEnvNames);

        await rig.Service.DeleteSessionSecretAsync("owner", old);
        Assert.Equal("", rig.TemplateEnvValue(ScheduledTemplateSecrets.NamesVariable));
    }

    [Fact]
    public async Task TheOwnersGrantOnAScheduledSession_IsAcceptedAndReachesTheNextRun()
    {
        var (rig, _, neu) = await Started();
        await using var __ = rig;

        await rig.Service.UpdateSessionAsync("owner", "sid", new UpdateSessionRequest { SecretGrants = [new(neu, false)] });

        Assert.Contains("NEW_NAME", rig.TemplateEnvNames);
        Assert.Contains("OLD_NAME", rig.TemplateEnvNames);

        await rig.Service.UpdateSessionAsync("owner", "sid", new UpdateSessionRequest { SecretGrants = [] });
        Assert.DoesNotContain("NEW_NAME", rig.TemplateEnvNames);
    }

    [Fact]
    public async Task AConcurrentChangeOfTheCronJob_IsReadAgainAndApplied()
    {
        var (rig, _, neu) = await Started();
        await using var __ = rig;
        rig.Fault = ScheduleFault.ReplaceTemplate; rig.FaultsLeft = 2;
        rig.SetProject(rig.Project("source") with { SecretIds = [neu] });

        await rig.Service.SyncSessionSecretsAsync("owner");

        Assert.Contains("NEW_NAME", rig.TemplateEnvNames);
    }

    [Fact]
    public async Task ATemplateThatCannotBeUpdated_IsReportedAndTheOwnerCanRetry()
    {
        var (rig, _, neu) = await Started();
        await using var __ = rig;
        rig.Fault = ScheduleFault.ReplaceTemplate;
        rig.SetProject(rig.Project("source") with { SecretIds = [neu] });

        // The project change itself is not failed over one schedule.
        await rig.Service.SyncSessionSecretsAsync("owner");

        Assert.Contains("OLD_NAME", rig.TemplateEnvNames);
        Assert.True((await rig.Service.GetSessionAsync("owner", "sid"))!.ScheduleSecretsOutdated);
        Assert.True(Assert.Single(await rig.Service.ListSessionsAsync("owner")).ScheduleSecretsOutdated);

        // The owner's retry while the cluster still refuses: an explained conflict, not a server error.
        await Assert.ThrowsAsync<InvalidOperationException>(() => rig.Service.ApplyResourcesAsync("owner", "sid"));

        rig.Fault = null;
        var applied = await rig.Service.ApplyResourcesAsync("owner", "sid");

        Assert.False(applied.ScheduleSecretsOutdated);
        Assert.Contains("NEW_NAME", rig.TemplateEnvNames);
        Assert.DoesNotContain("OLD_NAME", rig.TemplateEnvNames);
    }

    [Fact]
    public async Task ARunInProgress_KeepsItsPodAndOnlyLaterRunsChange()
    {
        var (rig, _, neu) = await Started();
        await using var __ = rig;
        rig.ActiveRun = true;
        rig.SetProject(rig.Project("source") with { SecretIds = [neu] });

        await rig.Service.SyncSessionSecretsAsync("owner");

        Assert.Empty(rig.Deleted);
        Assert.Contains("NEW_NAME", rig.TemplateEnvNames);
    }

    [Fact]
    public async Task AnUnfinishedMove_IsLeftToFinishTheTemplateItself()
    {
        var (rig, _, neu) = await Started();
        await using var __ = rig;
        rig.Annotate(KubernetesSessionService.ProjectMoveSuspendAnnotation, "false");
        rig.Annotate(KubernetesSessionService.ProjectMoveFromAnnotation, "source");
        rig.SetProject(rig.Project("source") with { SecretIds = [neu] });

        await rig.Service.SyncSessionSecretsAsync("owner");

        Assert.Contains("OLD_NAME", rig.TemplateEnvNames);
        var info = (await rig.Service.GetSessionAsync("owner", "sid"))!;
        Assert.True(info.ScheduleMoveUnfinished);
        Assert.False(info.ScheduleSecretsOutdated);
        await Assert.ThrowsAsync<InvalidOperationException>(() => rig.Service.ApplyResourcesAsync("owner", "sid"));
    }
}
