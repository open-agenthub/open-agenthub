using AgentHub.Api.Chat;
using Xunit;

namespace AgentHub.Api.Tests;

public class ChatFormattingTests
{
    [Fact]
    public void Tag_IsFirstFourChars() => Assert.Equal("a3f2", ChatFormatting.Tag("a3f2941be0c1"));

    [Theory]
    [InlineData("a3f2", "a3f2941be0c1", true)]   // exact tag
    [InlineData("a3f29", "a3f2941be0c1", true)]  // longer prefix
    [InlineData("a3", "a3f2941be0c1", true)]     // shorter prefix (caller ensures uniqueness)
    [InlineData("b7c1", "a3f2941be0c1", false)]
    [InlineData("", "a3f2941be0c1", false)]
    public void MatchesTag(string tag, string sessionId, bool expected)
        => Assert.Equal(expected, ChatFormatting.MatchesTag(tag, sessionId));

    [Fact]
    public void Split_ShortText_SingleChunk()
        => Assert.Equal(new[] { "hi" }, ChatFormatting.Split("hi", 100));

    [Fact]
    public void Split_BreaksAtLineBoundaries()
    {
        var text = string.Join("\n", Enumerable.Repeat("0123456789", 5)); // 54 chars
        var chunks = ChatFormatting.Split(text, 25);
        Assert.All(chunks, c => Assert.True(c.Length <= 25));
        Assert.Equal(text, string.Join("\n", chunks)); // lossless
        Assert.Equal(new[] { "0123456789\n0123456789", "0123456789\n0123456789", "0123456789" }, chunks);
    }

    [Fact]
    public void Split_HardSplitsOverlongSingleLine()
    {
        var chunks = ChatFormatting.Split(new string('x', 60), 25);
        Assert.Equal(3, chunks.Count);
        Assert.All(chunks, c => Assert.True(c.Length <= 25));
        Assert.Equal(new string('x', 60), string.Concat(chunks));
    }

    [Fact]
    public void Split_EmptyText_NoChunks() => Assert.Empty(ChatFormatting.Split("", 100));

    [Fact]
    public void Header_ContainsTagAndTitle()
    {
        var h = ChatFormatting.Header("a3f2941be0c1", "fix-login");
        Assert.Contains("#a3f2", h);
        Assert.Contains("fix-login", h);
    }

    [Fact]
    public void StatusText_MentionsPhaseAndPending()
    {
        var s = ChatFormatting.StatusText("Running", questionPending: true, pendingTool: "Bash", "https://x/s/1");
        Assert.Contains("Running", s);
        Assert.Contains("Bash", s);
        Assert.Contains("https://x/s/1", s);
    }
}
