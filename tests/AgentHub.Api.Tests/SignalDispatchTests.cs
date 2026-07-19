using AgentHub.Api.Chat.Signal;
using Xunit;

namespace AgentHub.Api.Tests;

public class SignalDispatchTests
{
    [Theory]
    [InlineData("👍", "allow")]
    [InlineData("👍🏽", "allow")] // skin-tone variant — same gesture
    [InlineData("👎", "deny")]
    [InlineData("👎🏻", "deny")]
    [InlineData("❤️", null)]
    [InlineData("🤔", null)]
    [InlineData("", null)]
    public void ReactionDecision_MapsThumbsToDecisions(string emoji, string? expected)
        => Assert.Equal(expected, SignalReceiveService.ReactionDecision(emoji));
}
