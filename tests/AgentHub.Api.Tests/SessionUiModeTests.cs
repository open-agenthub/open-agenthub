using AgentHub.Api.Models;
using Xunit;

namespace AgentHub.Api.Tests;

public sealed class SessionUiModeTests
{
    [Theory]
    [InlineData(null, "terminal")]
    [InlineData("", "terminal")]
    [InlineData("terminal", "terminal")]
    [InlineData("Terminal", "terminal")]
    [InlineData("chat", "chat")]
    [InlineData("CHAT", "chat")]
    [InlineData(" Chat ", "chat")]
    public void NormalizeForCreate_AcceptsSupportedValuesCaseInsensitively(string? input, string expected)
        => Assert.Equal(expected,
            SessionUiMode.NormalizeForCreate(input, AgentKind.Claude, SessionMode.Interactive));

    [Theory]
    [InlineData("tui")]
    [InlineData("chatty")]
    public void NormalizeForCreate_RejectsUnknownValues(string input)
    {
        var error = Assert.Throws<ArgumentException>(() =>
            SessionUiMode.NormalizeForCreate(input, AgentKind.Claude, SessionMode.Interactive));

        Assert.Contains("'terminal' or 'chat'", error.Message);
    }

    [Theory]
    [InlineData(AgentKind.Codex, SessionMode.Interactive)]
    [InlineData(AgentKind.Cursor, SessionMode.Interactive)]
    [InlineData(AgentKind.Claude, SessionMode.Autonomous)]
    [InlineData(AgentKind.Claude, SessionMode.Scheduled)]
    public void NormalizeForCreate_RejectsChatForUnsupportedAgentOrMode(AgentKind agent, SessionMode mode)
    {
        var error = Assert.Throws<ArgumentException>(() =>
            SessionUiMode.NormalizeForCreate(SessionUiMode.Chat, agent, mode));

        Assert.Contains("interactive Claude", error.Message);
    }

    [Theory]
    [InlineData(AgentKind.Codex, SessionMode.Autonomous)]
    [InlineData(AgentKind.Cursor, SessionMode.Scheduled)]
    public void NormalizeForCreate_AllowsTerminalForEveryAgentAndMode(AgentKind agent, SessionMode mode)
        => Assert.Equal(SessionUiMode.Terminal,
            SessionUiMode.NormalizeForCreate(SessionUiMode.Terminal, agent, mode));
}
