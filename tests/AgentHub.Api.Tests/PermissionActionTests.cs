using AgentHub.Api.Permissions;
using Xunit;

namespace AgentHub.Api.Tests;

public class PermissionActionTests
{
    [Fact]
    public void RoundTrips()
    {
        var id = PermissionAction.Id("allow", "abc123");
        Assert.Equal("perm:allow:abc123", id);
        Assert.True(PermissionAction.TryParse(id, out var d, out var r));
        Assert.Equal("allow", d);
        Assert.Equal("abc123", r);
    }

    [Theory]
    [InlineData("perm:allow:ab", "allow", "ab")]
    [InlineData("perm:deny:xy", "deny", "xy")]
    [InlineData("perm:allowAlways:9f", "allowAlways", "9f")]
    public void ParsesValid(string actionId, string decision, string reqId)
    {
        Assert.True(PermissionAction.TryParse(actionId, out var d, out var r));
        Assert.Equal(decision, d);
        Assert.Equal(reqId, r);
    }

    [Theory]
    [InlineData("")]
    [InlineData(null)]
    [InlineData("perm:allow")]        // too few parts
    [InlineData("other:allow:id")]    // wrong prefix
    [InlineData("perm::id")]          // empty decision
    [InlineData("perm:allow:")]       // empty id
    [InlineData("perm:garbage:id")]   // decision outside the allowlist
    [InlineData("perm:expired:id")]   // valid store value, but never a button decision
    [InlineData("perm:ALLOW:id")]     // allowlist is case-sensitive
    public void RejectsInvalid(string? actionId)
    {
        Assert.False(PermissionAction.TryParse(actionId, out _, out _));
    }
}
