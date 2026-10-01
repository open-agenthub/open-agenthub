using AgentHub.Api.Models;
using AgentHub.Api.Services;
using Xunit;

namespace AgentHub.Api.Tests;

public class TranscriptPollingTests
{
    [Fact]
    public void Page_FromZero_ReturnsEverythingAndACursorToContinueFrom()
    {
        var page = TranscriptPage.From("s1", "Running", "abcdef", offset: null, maxChars: null);

        Assert.Equal("abcdef", page.Text);
        Assert.Equal(0, page.Offset);
        Assert.Equal(6, page.NextOffset);
        Assert.Equal(6, page.Length);
        Assert.False(page.Truncated);
        Assert.True(page.Running);
    }

    [Fact]
    public void Page_AtTheEnd_ReturnsNothingButKeepsTheCursorStable()
    {
        // The common poll: nothing new since last time. An empty page must not move the cursor,
        // or a poller would skip the next output.
        var page = TranscriptPage.From("s1", "Running", "abcdef", offset: 6, maxChars: null);

        Assert.Equal("", page.Text);
        Assert.Equal(6, page.Offset);
        Assert.Equal(6, page.NextOffset);
        Assert.False(page.Truncated);
    }

    [Fact]
    public void Page_WithMaxChars_TruncatesAndSaysSoSoThePollerComesBack()
    {
        var page = TranscriptPage.From("s1", "Running", "abcdefghij", offset: 2, maxChars: 3);

        Assert.Equal("cde", page.Text);
        Assert.Equal(5, page.NextOffset);
        Assert.True(page.Truncated);

        var rest = TranscriptPage.From("s1", "Running", "abcdefghij", page.NextOffset, maxChars: 100);
        Assert.Equal("fghij", rest.Text);
        Assert.False(rest.Truncated);
    }

    [Fact]
    public void Page_WithAnOffsetPastAShrunkTranscript_ClampsInsteadOfFailing()
    {
        // The session agent keeps only the last 1 MB of scrollback and re-uploads that window, so a
        // transcript can get *shorter*. A cursor from before the trim must produce an empty page,
        // not an exception the poller can only treat as a dead session; the reported length is how
        // it notices its cursor is stale.
        var page = TranscriptPage.From("s1", "Running", "short", offset: 5_000, maxChars: null);

        Assert.Equal("", page.Text);
        Assert.Equal(5, page.Offset);
        Assert.Equal(5, page.NextOffset);
        Assert.Equal(5, page.Length);
    }

    [Fact]
    public void Page_WithHostileOffsetAndSize_IsClampedIntoRange()
    {
        var negative = TranscriptPage.From("s1", "Running", "abcdef", offset: -100, maxChars: -5);
        Assert.Equal(0, negative.Offset);
        Assert.Equal("a", negative.Text);

        var huge = TranscriptPage.From("s1", "Running", "abcdef", offset: 0, maxChars: int.MaxValue);
        Assert.Equal("abcdef", huge.Text);
    }

    [Theory]
    [InlineData("Pending", true)]
    [InlineData("Running", true)]
    [InlineData("Paused", true)]
    [InlineData("Scheduled", true)]
    [InlineData("Succeeded", false)]
    [InlineData("Failed", false)]
    [InlineData("failed", false)]
    [InlineData("SomethingNew", true)]
    public void Page_Running_IsFalseOnlyForPhasesTheSessionWillNotLeave(string phase, bool running)
    {
        // An unknown phase counts as running: telling a poller to stop on a phase we do not
        // recognise would abandon a session that is still working.
        Assert.Equal(running, TranscriptPage.From("s1", phase, "x", null, null).Running);
    }

    [Fact]
    public void Url_UsesTheConfiguredFrontendOriginAndTheSpaDeepLink()
    {
        Assert.Equal("https://agenthub.example.com/s/ab12cd",
            SessionUrl.For("https://agenthub.example.com", "ab12cd"));
        Assert.Equal("https://agenthub.example.com/s/ab12cd",
            SessionUrl.For("https://agenthub.example.com/", "ab12cd"));
    }

    [Fact]
    public void Url_WithoutConfiguration_IsNullRatherThanAGuess()
    {
        // A link handed to a person must not come from a forwarded Host header.
        Assert.Null(SessionUrl.For(null, "ab12cd"));
        Assert.Null(SessionUrl.For("", "ab12cd"));
        Assert.Null(SessionUrl.For("   ", "ab12cd"));
        Assert.Null(SessionUrl.For("https://agenthub.example.com", ""));
    }

    [Fact]
    public void Url_EscapesTheSessionId()
    {
        Assert.Equal("https://agenthub.example.com/s/a%2Fb",
            SessionUrl.For("https://agenthub.example.com", "a/b"));
    }

    [Fact]
    public void SystemPrompt_IsTrimmedAndEmptyBecomesNull()
    {
        Assert.Null(SessionSystemPrompt.Normalize(null));
        Assert.Null(SessionSystemPrompt.Normalize("   \n "));
        Assert.Equal("be terse", SessionSystemPrompt.Normalize("  be terse\n"));
    }

    [Fact]
    public void SystemPrompt_TooLong_IsRejectedBeforeItReachesThePodSpec()
    {
        // Left unchecked this surfaces as the Kubernetes API refusing an oversized pod spec, i.e. a
        // session that never starts for no stated reason.
        var error = Assert.Throws<ArgumentException>(() =>
            SessionSystemPrompt.Normalize(new string('x', SessionSystemPrompt.MaxLength + 1)));
        Assert.Contains(SessionSystemPrompt.MaxLength.ToString(), error.Message);

        Assert.Equal(SessionSystemPrompt.MaxLength,
            SessionSystemPrompt.Normalize(new string('x', SessionSystemPrompt.MaxLength))!.Length);
    }
}
