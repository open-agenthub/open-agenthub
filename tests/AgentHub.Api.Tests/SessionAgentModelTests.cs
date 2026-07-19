using AgentHub.Api.Models;
using Xunit;

namespace AgentHub.Api.Tests;

public sealed class SessionAgentModelTests
{
    [Fact]
    public void NewRequest_DefaultsToClaudeSubscription()
    {
        var request = new CreateSessionRequest();

        Assert.Equal(AgentKind.Claude, request.Agent);
        Assert.Equal(AgentAuthMode.Subscription, request.AuthMode);
    }
}
