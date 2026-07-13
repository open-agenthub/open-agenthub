using AgentHub.Api.Ee.Slack;
using AgentHub.Api.Persistence;
using Xunit;

namespace AgentHub.Api.Tests;

public class SlackTargetTests
{
    private static AppUser User(bool enabled = true, string? email = null, string? channel = null)
        => new("maik", email, "Maik", enabled, channel);

    [Fact]
    public void OptedOut_NoTarget()
        => Assert.Equal((null, false), SlackTarget.Decide(User(enabled: false, email: "a@b.c"), "Cfallback"));

    [Fact]
    public void Override_WinsOverEmail()
        => Assert.Equal(("C999", false), SlackTarget.Decide(User(email: "a@b.c", channel: "C999"), "Cfallback"));

    [Fact]
    public void Email_TriggersLookup()
        => Assert.Equal((null, true), SlackTarget.Decide(User(email: "a@b.c"), "Cfallback"));

    [Fact]
    public void NoEmail_UsesFallback()
        => Assert.Equal(("Cfallback", false), SlackTarget.Decide(User(), "Cfallback"));

    [Fact]
    public void NoEmail_NoFallback_NoTarget()
        => Assert.Equal((null, false), SlackTarget.Decide(User(), ""));

    [Fact]
    public void UnknownUser_UsesFallback()
        => Assert.Equal(("Cfallback", false), SlackTarget.Decide(null, "Cfallback"));
}
