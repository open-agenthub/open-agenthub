using AgentHub.Api.Services;
using Xunit;

namespace AgentHub.Api.Tests;

public class SlackAnsiTests
{
    [Fact]
    public void StripsCsiColorCodes()
    {
        var esc = ((char)27).ToString();
        var input = $"{esc}[31mred{esc}[0m plain";
        Assert.Equal("red plain", AgentTerminal.StripAnsi(input));
    }

    [Fact]
    public void NormalizesCarriageReturns()
    {
        Assert.Equal("a\nb\nc", AgentTerminal.StripAnsi("a\r\nb\rc"));
    }

    [Fact]
    public void LeavesPlainTextUntouched()
    {
        Assert.Equal("hello [world] 42", AgentTerminal.StripAnsi("hello [world] 42"));
    }
    [Fact]
    public void StripsClaudeTrustScreenTerminalControls()
    {
        var esc = ((char)27).ToString();
        var input = $"{esc}7{esc}[r{esc}8{esc}[?25h{esc}[?25l{esc}[?2004h{esc}[?1004h{esc}[?2031h"
            + $"{esc}[38;5;220m────{esc}[39m\n"
            + $"{esc}[2G{esc}[1mAccessing workspace:{esc}[22m{esc}[39m";

        Assert.Equal("────\nAccessing workspace:", AgentTerminal.StripAnsi(input));
    }
}
