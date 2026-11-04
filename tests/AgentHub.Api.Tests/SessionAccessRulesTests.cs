using AgentHub.Api.Ee.Sharing;
using Xunit;

namespace AgentHub.Api.Tests;

public class SessionAccessRulesTests
{
    private static ShareGrant Own(ShareRole role) => new(role, ShareGrantScope.Session, "self");
    private static ShareGrant Up(ShareRole role, string from, int depth) => new(role, ShareGrantScope.Ancestor, from, depth);

    [Fact]
    public void Owner_WinsOverEveryGrant()
    {
        var decision = SessionAccessRules.Decide(owns: true, [Own(ShareRole.Viewer)]);
        Assert.Equal(SessionAccessLevel.Owner, decision.Level);
        Assert.Null(decision.Via);
    }

    [Fact]
    public void NoGrant_IsNoAccess()
        => Assert.Equal(SessionAccessLevel.None, SessionAccessRules.Decide(false, []).Level);

    [Fact]
    public void StrongestRole_Wins_AndATieReportsTheMostSpecificGrant()
    {
        var stronger = SessionAccessRules.Decide(false,
            [Own(ShareRole.Viewer), Up(ShareRole.Collaborator, "far", 3), Up(ShareRole.Collaborator, "near", 1)]);
        Assert.Equal(SessionAccessLevel.Collaborator, stronger.Level);
        Assert.Equal("near", stronger.Via!.SessionId);

        var tie = SessionAccessRules.Decide(false, [Up(ShareRole.Viewer, "parent", 1), Own(ShareRole.Viewer)]);
        Assert.Equal(ShareGrantScope.Session, tie.Via!.Scope);
    }
}

public class ProjectGrantRulesTests
{
    private static ShareGrant Own(ShareRole role) => new(role, ShareGrantScope.Session, "self");
    private static ShareGrant Project(ShareRole role, string session = "self", int depth = 0)
        => new(role, ShareGrantScope.Project, session, depth, "p1", "Release");

    [Fact]
    public void ASessionShare_AddsToAProjectShare_ButNeverNarrowsIt()
    {
        var narrower = SessionAccessRules.Decide(false, [Project(ShareRole.Collaborator), Own(ShareRole.Viewer)]);
        Assert.Equal(SessionAccessLevel.Collaborator, narrower.Level);
        Assert.Equal(ShareGrantScope.Project, narrower.Via!.Scope);

        var wider = SessionAccessRules.Decide(false, [Project(ShareRole.Viewer), Own(ShareRole.Collaborator)]);
        Assert.Equal(SessionAccessLevel.Collaborator, wider.Level);
        Assert.Equal(ShareGrantScope.Session, wider.Via!.Scope);
    }

    [Fact]
    public void ATie_ReportsTheSessionShare_BeforeTheProject()
        => Assert.Equal(ShareGrantScope.Session,
            SessionAccessRules.Decide(false, [Project(ShareRole.Viewer), Own(ShareRole.Viewer)]).Via!.Scope);

    [Fact]
    public void ForRecipient_NamesTheProject_AndTheAncestorOnlyWhenTheProjectIsAnAncestors()
    {
        var session = new AgentHub.Api.Persistence.SessionRecord { Id = "self", Owner = "alice", Title = "t", CallbackToken = "c" };

        var own = SessionAccessRules.ForRecipient(session, [Project(ShareRole.Viewer)])!;
        Assert.Equal("Release", own.SharedViaProject);
        Assert.Null(own.InheritedFrom);

        var up = SessionAccessRules.ForRecipient(session, [Project(ShareRole.Viewer, "parent", 1)])!;
        Assert.Equal("Release", up.SharedViaProject);
        Assert.Equal("parent", up.InheritedFrom);
    }
}
