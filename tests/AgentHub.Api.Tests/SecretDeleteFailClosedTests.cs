using AgentHub.Api.Controllers;
using AgentHub.Api.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using System.Security.Claims;
using Xunit;

namespace AgentHub.Api.Tests;

/// <summary>
/// Deleting a secret fails closed (#303): a reported success means no session copy still holds the
/// value. A copy that cannot be rewritten is withdrawn; only when that fails too is the delete
/// answered as pending, and repeating it finishes the job.
/// </summary>
public class SecretDeleteFailClosedTests
{
    private static readonly string Env = SessionSecretDeliveryFactory.EnvSecretName("sid");
    private static readonly string Files = SessionSecretDeliveryFactory.FilesSecretName("sid");

    private static async Task<(ScheduleClusterRig Rig, string Old)> Started()
    {
        var rig = await ScheduleClusterRig.Start();
        rig.SeedCopies("OLD_NAME");
        return (rig, rig.Project("source").SecretIds![0]);
    }

    [Fact]
    public async Task ACopyThatCannotBeRewritten_IsWithdrawn_SoTheDeleteIsTrue()
    {
        var (rig, old) = await Started();
        await using var _ = rig;
        rig.Fault = ScheduleFault.WriteSecretCopies;

        await rig.Service.DeleteSessionSecretAsync("owner", old);

        // No copy, so no value: the scheduled run's references are optional and resolve to nothing.
        Assert.False(rig.HasSecret(Env));
        Assert.False(rig.HasSecret(Files));

        // Not silent: the session says its secrets are withdrawn, in its own view and in the list.
        Assert.True((await rig.Service.GetSessionAsync("owner", "sid"))!.SecretsWithdrawn);
        Assert.True(Assert.Single(await rig.Service.ListSessionsAsync("owner")).SecretsWithdrawn);

        // The owner's retry, while the copies still cannot be written, reports that rather than success.
        await Assert.ThrowsAsync<SessionSecretCopiesPendingException>(async () =>
        {
            rig.RefuseCopyDeletes = true;
            try { await rig.Service.ResyncSessionSecretsAsync("owner", "sid"); }
            finally { rig.RefuseCopyDeletes = false; }
        });

        // A scheduled session is synced by its record, so its copies come back without the value.
        rig.Fault = null;
        var resynced = await rig.Service.ResyncSessionSecretsAsync("owner", "sid");
        Assert.False(resynced.SecretsWithdrawn);
        Assert.True(rig.HasSecret(Env));
        Assert.False(rig.EnvCopy.ContainsKey("OLD_NAME"));
    }

    [Fact]
    public async Task ACopyThatCanBeNeitherRewrittenNorWithdrawn_IsReportedAndARepeatedDeleteFinishes()
    {
        var (rig, old) = await Started();
        await using var _ = rig;
        rig.Fault = ScheduleFault.WriteSecretCopies; rig.RefuseCopyDeletes = true;

        var pending = await Assert.ThrowsAsync<SessionSecretCopiesPendingException>(() => rig.Service.DeleteSessionSecretAsync("owner", old));
        Assert.Equal(1, pending.Sessions);
        // The honest state: the copy does still hold it, which is why success was not reported.
        Assert.True(rig.EnvCopy.ContainsKey("OLD_NAME"));
        Assert.DoesNotContain(await rig.Service.ListSessionSecretsAsync("owner"), s => s.Id == old);

        rig.Fault = null; rig.RefuseCopyDeletes = false;
        // The id is already gone from the store; the repeat must still run the sync.
        await rig.Service.DeleteSessionSecretAsync("owner", old);
        Assert.False(rig.EnvCopy.ContainsKey("OLD_NAME"));
    }

    [Fact]
    public async Task APendingDelete_IsA503TheSettingsPageCanRecognise()
    {
        var service = RuntimeProofProxy.Make<ISessionService>((method, _) => method.Name == nameof(ISessionService.DeleteSessionSecretAsync)
            ? Task.FromException(new SessionSecretCopiesPendingException(2)) : null);
        var controller = new SecretsController(service) { ControllerContext = new() { HttpContext = new DefaultHttpContext {
            User = new ClaimsPrincipal(new ClaimsIdentity([new Claim("preferred_username", "owner")], "test")) } } };

        var result = Assert.IsType<ObjectResult>(await controller.Delete("e1", default));

        Assert.Equal(StatusCodes.Status503ServiceUnavailable, result.StatusCode);
        Assert.Contains("secret_copies_pending", System.Text.Json.JsonSerializer.Serialize(result.Value));
    }
}
