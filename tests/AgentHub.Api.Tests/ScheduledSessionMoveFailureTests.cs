using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AgentHub.Api.Agents;
using AgentHub.Api.Browser;
using AgentHub.Api.Library;
using AgentHub.Api.Models;
using AgentHub.Api.Persistence;
using AgentHub.Api.Services;
using AgentHub.Api.Storage;
using AgentHub.Api.Usage;
using k8s;
using k8s.Models;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AgentHub.Api.Tests;

/// <summary>
/// A scheduled move that fails after the schedule was suspended (#295). Before the record changes,
/// the schedule is rolled back as the owner left it. After, it stays suspended and is reported as
/// unfinished, and a retry completes it. It is never left suspended without either.
/// </summary>
public class ScheduledSessionMoveFailureTests
{
    [Theory]
    [InlineData(ScheduleFault.ListRuns)]
    [InlineData(ScheduleFault.StoreRecord)]
    public async Task AFailureBeforeTheRecordChanges_RollsTheScheduleBack(ScheduleFault step)
    {
        await using var rig = await ScheduleClusterRig.Start();
        rig.Fault = step;
        await Assert.ThrowsAnyAsync<Exception>(() => rig.Service.MoveScheduledSessionAsync("owner", "sid", "target", false));

        Assert.Equal("source", rig.Record.ProjectId);
        Assert.False(rig.Suspended);
        Assert.Empty(rig.MoveAnnotations);
        Assert.Contains("OLD_NAME", rig.TemplateEnvNames);
        Assert.False((await rig.Service.GetSessionAsync("owner", "sid"))!.ScheduleMoveUnfinished);
    }

    [Theory]
    [InlineData(ScheduleFault.WriteSecretCopies)]
    [InlineData(ScheduleFault.ReplaceTemplate)]
    public async Task AFailureAfterTheRecordChanges_StaysSuspendedVisiblyUntilARetryFinishes(ScheduleFault step)
    {
        await using var rig = await ScheduleClusterRig.Start();
        rig.Fault = step;
        await Assert.ThrowsAnyAsync<Exception>(() => rig.Service.MoveScheduledSessionAsync("owner", "sid", "target", false));

        Assert.Equal("target", rig.Record.ProjectId);
        // Unsuspending here would start the next run with the source's variable names while the
        // record already grants the destination's files.
        Assert.True(rig.Suspended);
        Assert.Contains("OLD_NAME", rig.TemplateEnvNames);
        Assert.True((await rig.Service.GetSessionAsync("owner", "sid"))!.ScheduleMoveUnfinished);
        Assert.True(Assert.Single(await rig.Service.ListSessionsAsync("owner")).ScheduleMoveUnfinished);

        // A failed retry before its own record write must not roll back to the source template.
        rig.Fault = ScheduleFault.ListRuns;
        await Assert.ThrowsAnyAsync<Exception>(() => rig.Service.MoveScheduledSessionAsync("owner", "sid", "source", false));
        Assert.True(rig.Suspended);
        Assert.NotEmpty(rig.MoveAnnotations);

        rig.Fault = null;
        var finished = await rig.Service.MoveScheduledSessionAsync("owner", "sid", "target", false);

        Assert.False(finished.ScheduleMoveUnfinished);
        Assert.False(rig.Suspended);
        Assert.Empty(rig.MoveAnnotations);
        Assert.Contains("NEW_NAME", rig.TemplateEnvNames);
        Assert.DoesNotContain("OLD_NAME", rig.TemplateEnvNames);
        Assert.Equal("new-value", rig.EnvCopy["NEW_NAME"]);
        Assert.False(rig.EnvCopy.ContainsKey("OLD_NAME"));
    }

    [Fact]
    public async Task AScheduleTheOwnerHadSuspended_StaysSuspendedAfterTheMove()
    {
        await using var rig = await ScheduleClusterRig.Start(suspended: true);
        await rig.Service.MoveScheduledSessionAsync("owner", "sid", "target", false);
        Assert.True(rig.Suspended);
        Assert.Empty(rig.MoveAnnotations);
        Assert.Contains("NEW_NAME", rig.TemplateEnvNames);
    }

    [Fact]
    public async Task AnActiveRun_IsRefusedAndTheScheduleRestored_OrStoppedOnRequest()
    {
        await using var rig = await ScheduleClusterRig.Start();
        rig.ActiveRun = true;
        await Assert.ThrowsAsync<ScheduledRunActiveException>(() => rig.Service.MoveScheduledSessionAsync("owner", "sid", "target", false));
        Assert.Equal("source", rig.Record.ProjectId);
        Assert.False(rig.Suspended);
        Assert.Empty(rig.MoveAnnotations);

        await rig.Service.MoveScheduledSessionAsync("owner", "sid", "target", true);
        Assert.Equal(["job/run-1", "pod/run-1-pod"], rig.Deleted);
        Assert.Equal("target", rig.Record.ProjectId);
        Assert.Contains("NEW_NAME", rig.TemplateEnvNames);
    }
}
