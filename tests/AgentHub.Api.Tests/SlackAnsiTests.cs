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
}
