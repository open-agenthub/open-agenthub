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
