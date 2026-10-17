using AgentHub.Api.Controllers;
using AgentHub.Api.Ee.Library;
using AgentHub.Api.Library;
using AgentHub.Api.Models;
using AgentHub.Api.Persistence;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Xunit;

namespace AgentHub.Api.Tests;

/// <summary>
/// The skills endpoint is polled by every running session, so an unchanged library has to be
/// answered with a 304 and anything that changes what the pod would materialise has to change
/// the validator.
/// </summary>
public class InternalSkillsSyncTests
{
    private const string SessionId = "sess-1";
    private const string Token = "tok-1";

    [Fact]
    public async Task UnchangedLibrary_AnswersNotModified()
    {
        var (access, skills, _) = Build();
        skills.Add("alice", "deploy", content: "# deploy");

        var first = Controller(access);
        Assert.IsType<OkObjectResult>(await first.Skills(SessionId, CancellationToken.None));
        var etag = first.Response.Headers.ETag.ToString();
        Assert.Matches("^\"[0-9a-f]{32}\"$", etag);

        var second = Controller(access, etag);
        var result = await second.Skills(SessionId, CancellationToken.None);
        Assert.Equal(StatusCodes.Status304NotModified, Assert.IsType<StatusCodeResult>(result).StatusCode);
        Assert.Equal(etag, second.Response.Headers.ETag.ToString());
    }

    [Fact]
    public async Task ChangedLibrary_AnswersWithPayloadAndNewValidator()
    {
        var (access, skills, _) = Build();
        var skill = skills.Add("alice", "deploy", content: "# deploy");
        var first = Controller(access);
        await first.Skills(SessionId, CancellationToken.None);
        var etag = first.Response.Headers.ETag.ToString();

        await skills.UpdateAsync("alice", skill.Id,
            new SaveSkillRequest("deploy", null, "# deploy v2"));

        var second = Controller(access, etag);
        var ok = Assert.IsType<OkObjectResult>(await second.Skills(SessionId, CancellationToken.None));
        Assert.Contains("deploy v2", System.Text.Json.JsonSerializer.Serialize(ok.Value));
        Assert.NotEqual(etag, second.Response.Headers.ETag.ToString());
    }

    [Fact]
    public async Task Version_MovesWithEveryChangeThePodWouldSee()
    {
        var (access, skills, shares) = Build();
        var own = skills.Add("alice", "deploy");
        var v0 = await access.SkillPayloadsVersionAsync("alice", null);
        Assert.Equal(v0, await access.SkillPayloadsVersionAsync("alice", null));

        // Another user's unshared skill is invisible, so it must not cost the pod a download.
        var foreign = skills.Add("bob", "theirs");
        Assert.Equal(v0, await access.SkillPayloadsVersionAsync("alice", null));

        await shares.SetSharesAsync(LibraryItemTypes.Skill, foreign.Id, all: true, null, null, "bob");
        var shared = await access.SkillPayloadsVersionAsync("alice", null);
        Assert.NotEqual(v0, shared);

        await skills.UpdateAsync("alice", own.Id, new SaveSkillRequest("deploy", null, "# v2"));
        var updated = await access.SkillPayloadsVersionAsync("alice", null);
        Assert.NotEqual(shared, updated);

        await skills.DeleteAsync("alice", own.Id);
        Assert.NotEqual(updated, await access.SkillPayloadsVersionAsync("alice", null));
    }

    [Fact]
    public async Task Version_FollowsTheNameConflictWinner()
    {
        var (access, skills, shares) = Build();
        var foreign = skills.Add("bob", "deploy", content: "# theirs");
        await shares.SetSharesAsync(LibraryItemTypes.Skill, foreign.Id, all: true, null, null, "bob");
        var theirs = await access.SkillPayloadsVersionAsync("alice", null);

        // Same name, own skill now wins: the pod has to replace the directory's content.
        skills.Add("alice", "deploy", content: "# mine");
        Assert.NotEqual(theirs, await access.SkillPayloadsVersionAsync("alice", null));
    }

    private static (LibraryAccessService Access, InMemorySkillStore Skills, InMemoryLibraryShareStore Shares) Build()
    {
        var skills = new InMemorySkillStore();
        var shares = new InMemoryLibraryShareStore();
        shares.KnownUsers.Add("alice");
        var access = new LibraryAccessService(new InMemoryMcpServerStore(), skills, shares,
            new FakeEnterpriseLicense(true));
        return (access, skills, shares);
    }

    private static InternalController Controller(ILibraryAccess access, string? ifNoneMatch = null)
    {
        var session = new SessionRecord
        {
            Id = SessionId, Owner = "alice", CallbackToken = Token, Mode = SessionMode.Interactive, Title = "s"
        };
        var controller = new InternalController(
            new InternalResourcesTests.SingleSessionStore(session), [], null!, null!, [], [], null!, access)
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() }
        };
        controller.Request.Headers["X-Agent-Token"] = Token;
        if (ifNoneMatch is not null) controller.Request.Headers.IfNoneMatch = ifNoneMatch;
        return controller;
    }
}
