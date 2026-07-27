using System.Security.Claims;
using AgentHub.Api.Ee.Identity;
using Xunit;

namespace AgentHub.Api.Tests;

public class GroupClaimsTests
{
    [Fact]
    public void MultipleClaims_OnePerGroup()
    {
        var groups = GroupClaims.Extract(new[] { "devs", "ops" });
        Assert.Equal(new[] { "devs", "ops" }, groups);
    }

    [Fact]
    public void SingleClaim_WithJsonArrayValue()
    {
        var groups = GroupClaims.Extract(new[] { """["devs", "ops"]""" });
        Assert.Equal(new[] { "devs", "ops" }, groups);
    }

    [Fact]
    public void KeycloakGroupPaths_LoseLeadingSlash()
    {
        var groups = GroupClaims.Extract(new[] { "/team-a", "/parent/child" });
        Assert.Equal(new[] { "team-a", "parent/child" }, groups);
    }

    [Fact]
    public void Deduplicates_AndDropsEmpties()
    {
        var groups = GroupClaims.Extract(new[] { "devs", " devs ", "", "  ", "/devs" });
        Assert.Equal(new[] { "devs" }, groups);
    }

    [Fact]
    public void MalformedJsonArray_IsTreatedAsLiteralName()
    {
        var groups = GroupClaims.Extract(new[] { "[not-json" });
        Assert.Equal(new[] { "[not-json" }, groups);
    }

    [Fact]
    public void ReadsConfiguredClaim_FromPrincipal()
    {
        var identity = new ClaimsIdentity(new[]
        {
            new Claim("my-groups", "alpha"),
            new Claim("my-groups", "beta"),
            new Claim("groups", "ignored-default-claim")
        }, "test");
        var groups = GroupClaims.Extract(new ClaimsPrincipal(identity), "my-groups");
        Assert.Equal(new[] { "alpha", "beta" }, groups);
    }

    [Fact]
    public void NoClaims_YieldsEmpty()
        => Assert.Empty(GroupClaims.Extract(new ClaimsPrincipal(new ClaimsIdentity())));
}
