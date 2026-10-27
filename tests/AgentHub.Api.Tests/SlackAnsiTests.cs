using AgentHub.Api.Services;
using Xunit;

namespace AgentHub.Api.Tests;

public class SlackAnsiTests
{
    [Theory]
    [InlineData("Try\u001b[C\"refactor\u001b[C<filepath>\"", "Try \"refactor <filepath>\"")]
    [InlineData("auto\u001b[1Cmode\u001b[Con (shift+tab\u001b[Cto\u001b[Ccycle)", "auto mode on (shift+tab to cycle)")]
    [InlineData("a\u001b[4Gb", "a  b")]
    [InlineData("a\u009b0Cb", "a b")]
    public void PreservesCursorMotionSpaces(string input, string expected)
    {
        Assert.Equal(expected, AgentTerminal.CleanTranscript(input));
    }

    [Fact]
    public void CursorGaps_HaveBoundedExpansion()
    {
        Assert.Equal(4098, AgentTerminal.StripAnsi("a\u001b[2147483647Cb").Length);
        Assert.Equal("ab", AgentTerminal.StripAnsi("a\u001b[999999999999999999Cb"));
    }

    [Fact]
    public void RealClaudeStartup_PreservesRenderedCellGaps()
    {
        var fixture = System.Text.Json.JsonDocument.Parse(File.ReadAllText(
            Path.Combine(AppContext.BaseDirectory, "Fixtures", "claude-startup.json"))).RootElement;
        var text = AgentTerminal.CleanTranscript(fixture.GetProperty("output").GetString())!;
        var screen = fixture.GetProperty("screen").GetString()!;
        Assert.Contains("Claude Code v2.1.287", screen);
        Assert.Contains("Claude Code v2.1.287", text);
        Assert.Contains("auto mode on (shift+tab to cycle)", text);
        Assert.Contains("  bit (owner 0:1000, mode 2777)", text);
    }

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

        Assert.Equal("────\n Accessing workspace:", AgentTerminal.StripAnsi(input));
    }

    [Fact]
    public void CleanTranscript_PreservesMissingTranscriptAndSanitizesStoredText()
    {
        var esc = ((char)27).ToString();

        Assert.Null(AgentTerminal.CleanTranscript(null));
        Assert.Equal("stored text", AgentTerminal.CleanTranscript($"{esc}[31mstored text{esc}[0m"));
    }
    [Theory]
    [InlineData("\u001b[>4;2mtext", "text")]
    [InlineData("\u009b31mred\u009b0m", "red")]
    [InlineData("\u009d0;title\u0007text", "text")]
    [InlineData("\u009d0;title\u009ctext", "text")]
    public void StripsSevenAndEightBitTerminalControlSequences(string input, string expected)
    {
        Assert.Equal(expected, AgentTerminal.StripAnsi(input));
    }
}
