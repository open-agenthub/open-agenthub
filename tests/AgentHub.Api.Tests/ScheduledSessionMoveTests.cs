using System.Security.Claims;
using AgentHub.Api.Controllers;
using AgentHub.Api.Models;
using AgentHub.Api.Persistence;
using AgentHub.Api.Services;
using k8s;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Xunit;

namespace AgentHub.Api.Tests;

/// <summary>Moving a scheduled session between projects (#295, docs/session-organization.md).</summary>
public class ScheduledSessionMoveTests
{
    [Fact]
    public void ReplacingTheSecretBlock_GivesTheTemplateAFreshSpawnWouldHave()
    {
        // The template is not rebuilt, so the replaced block must be exactly what the factory
        // writes for the destination — otherwise the next run differs from a recreated session.
        var source = SessionSecretPodSpecTests.Build(withSecrets: true, envNames: ["OLD_A", "SHARED"]);
        var destination = SessionSecretPodSpecTests.Build(withSecrets: true, envNames: ["NEW_B", "SHARED"]);

        var (index, env) = ScheduledTemplateSecrets.Replace(source, "sid", ["NEW_B", "SHARED"]);

        Assert.Equal(KubernetesJson.Serialize(destination.Containers[index].Env), KubernetesJson.Serialize(env));
        Assert.DoesNotContain(env, e => e.Name == "OLD_A");
    }

    [Fact]
    public void ReplacingWithNothing_LeavesNoReferenceIntoTheVariableCopy()
    {
        var source = SessionSecretPodSpecTests.Build(withSecrets: true, envNames: ["OLD_A"]);
        var (_, env) = ScheduledTemplateSecrets.Replace(source, "sid", []);
        Assert.DoesNotContain(env, e => e.ValueFrom?.SecretKeyRef is not null && e.ValueFrom.SecretKeyRef.Name == "sessionenv-sid");
        Assert.Equal("", Assert.Single(env, e => e.Name == ScheduledTemplateSecrets.NamesVariable).Value);
    }

    [Fact]
    public void ATemplateFromBeforeSessionSecrets_IsRefusedRatherThanGuessed()
    {
        var old = SessionSecretPodSpecTests.Build(withSecrets: false);
        Assert.Throws<InvalidOperationException>(() => ScheduledTemplateSecrets.Replace(old, "sid", ["NEW_B"]));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AScheduledSession_TakesTheScheduledMove_WithTheOwnersChoiceAboutARun(bool stop)
    {
        var service = new Recording();
        var result = await Controller(SessionMode.Scheduled, service).Move("sid", new("target", true, stop), default);
        Assert.IsType<OkObjectResult>(result.Result);
        Assert.Equal(("sid", "target", stop), service.Scheduled);
        Assert.False(service.Updated);
    }

    [Fact]
    public async Task ARunInProgress_IsAConflictTheDialogCanRecognise()
    {
        var service = new Recording { RunActive = true };
        var result = await Controller(SessionMode.Scheduled, service).Move("sid", new("target", true), default);
        var conflict = Assert.IsType<ConflictObjectResult>(result.Result);
        Assert.Contains("scheduled_run_active", System.Text.Json.JsonSerializer.Serialize(conflict.Value));
    }

    [Fact]
    public async Task AnInteractiveSession_KeepsThePauseAndMovePath()
    {
        var service = new Recording();
        await Controller(SessionMode.Interactive, service).Move("sid", new("target", true), default);
        Assert.True(service.Updated);
        Assert.Null(service.Scheduled);
    }

    [Theory]
    [InlineData("other-owner", "target")]
    [InlineData("owner", "foreign")]
    public async Task AForeignSessionOrProject_IsNotFound(string caller, string project)
    {
        var service = new Recording();
        var result = await Controller(SessionMode.Scheduled, service, caller).Move("sid", new(project, true), default);
        Assert.IsType<NotFoundResult>(result.Result);
        Assert.Null(service.Scheduled);
    }

    private static SessionOrganizationController Controller(SessionMode mode, Recording service, string caller = "owner")
    {
        var record = new SessionRecord { Id = "sid", Owner = "owner", CallbackToken = "t", Mode = mode, ProjectId = "source" };
        var records = RuntimeProofProxy.Make<ISessionStore>((method, args) => method.Name == "GetAsync"
            ? Task.FromResult(args![0] as string == "owner" && args[1] as string == "sid" ? record : null) : null);
        var projects = RuntimeProofProxy.Make<IProjectStore>((method, args) => method.Name == "GetAsync"
            ? Task.FromResult(args![0] as string == "owner" && args[1] as string is "source" or "target"
                ? new ProjectInfo((string)args[1]!, "Project", null, 0) : null) : null);
        return new SessionOrganizationController(null!, projects, records, service.Service())
        {
            ControllerContext = new() { HttpContext = new DefaultHttpContext {
                User = new ClaimsPrincipal(new ClaimsIdentity([new Claim("preferred_username", caller)], "test")) } }
        };
    }

    private sealed class Recording
    {
        public (string Id, string? Project, bool Stop)? Scheduled;
        public bool Updated, RunActive;
        private static readonly SessionInfo Info = new() { Id = "sid", Title = "s", Owner = "owner", Mode = SessionMode.Scheduled, Phase = "Scheduled" };

        public ISessionService Service() => RuntimeProofProxy.Make<ISessionService>((method, args) => method.Name switch
        {
            nameof(ISessionService.MoveScheduledSessionAsync) => RunActive
                ? throw new ScheduledRunActiveException("A scheduled run is in progress.")
                : Record(() => Scheduled = ((string)args![1]!, (string?)args[2], (bool)args[3]!)),
            nameof(ISessionService.UpdateSessionAsync) => Record(() => Updated = true),
            _ => null
        });
        private static Task<SessionInfo> Record(Action action) { action(); return Task.FromResult(Info); }
    }
}
