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
    public void FindByTag_UniquePrefix_ReturnsMatch()
    {
        var (match, count) = ChatFormatting.FindByTag("a3",
            new[] { "a3f2941be0c1", "b7c1550d2e9f" }, s => s);
        Assert.Equal(1, count);
        Assert.Equal("a3f2941be0c1", match);
    }

    [Fact]
    public void FindByTag_AmbiguousPrefix_CountsButNoMatch()
    {
        var (match, count) = ChatFormatting.FindByTag("a3f2",
            new[] { "a3f2941be0c1", "a3f2000d2e9f", "b7c1550d2e9f" }, s => s);
        Assert.Equal(2, count);
        Assert.Null(match);
    }

    [Fact]
    public void FindByTag_NoMatch_CountZero()
    {
        var (match, count) = ChatFormatting.FindByTag("zz",
            new[] { "a3f2941be0c1" }, s => s);
        Assert.Equal(0, count);
        Assert.Null(match);
    }

    [Fact]
    public void Split_ShortText_SingleChunk()
        => Assert.Equal(new[] { "hi" }, ChatFormatting.Split("hi", 100));

    [Fact]
    public void Split_BreaksAtLineBoundaries()
    {
        var text = string.Join("\n", Enumerable.Repeat("0123456789", 5)); // 54 chars
        var chunks = ChatFormatting.Split(text, 25);
        Assert.All(chunks, c => Assert.True(c.Length <= 25));
        Assert.Equal(text, string.Join("\n", chunks)); // content preserved
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
    public void Split_DoesNotCutSurrogatePairs()
    {
        var chunks = ChatFormatting.Split(new string('a', 4) + "😀😀", 5);
        Assert.All(chunks, c => Assert.False(char.IsHighSurrogate(c[^1])));
        Assert.Equal("aaaa😀😀", string.Concat(chunks));

        var atCut = ChatFormatting.Split("😀😀😀", 5);
        Assert.All(atCut, c => Assert.False(char.IsHighSurrogate(c[^1])));
        Assert.Equal("😀😀😀", string.Concat(atCut));
    }

    [Fact]
    public void Split_RejectsNonPositiveMaxLen()
        => Assert.Throws<ArgumentOutOfRangeException>(() => ChatFormatting.Split("x", 0));

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

    [Theory]
    [InlineData("!new fix the login bug", true, "fix the login bug")]
    [InlineData("/new fix the login bug", true, "fix the login bug")]
    [InlineData("  !NEW   spaced out  ", true, "spaced out")]
    [InlineData("!new", true, "")]                    // usage hint case
    [InlineData("!newer sessions", false, "")]        // not the command
    [InlineData("please !new thing", false, "")]      // must start the message
    [InlineData("just a reply", false, "")]
    public void TryParseNewCommand_RecognizesOnlyTheCommand(string text, bool expected, string expectedPrompt)
    {
        Assert.Equal(expected, ChatFormatting.TryParseNewCommand(text, out var prompt));
        Assert.Equal(expectedPrompt, prompt);
    }

    [Theory]
    [InlineData("!repos", true, "")]
    [InlineData("/repos", true, "")]
    [InlineData("/repos agenthub", true, "agenthub")]
    [InlineData("  !REPOS  front  ", true, "front")]
    [InlineData("!projects tool", true, "tool")]       // alias
    [InlineData("!repository", false, "")]             // not the command
    [InlineData("show !repos", false, "")]             // must start the message
    public void TryParseReposCommand_RecognizesOnlyTheCommand(string text, bool expected, string expectedQuery)
    {
        Assert.Equal(expected, ChatFormatting.TryParseReposCommand(text, out var query));
        Assert.Equal(expectedQuery, query);
    }

    [Fact]
    public void SplitRepoTokens_TakesLeadingPlusTokensOnly()
    {
        var (tokens, rest) = ChatFormatting.SplitRepoTokens("+app +acme/tool#dev fix the bug");
        Assert.Equal(new[] { "app", "acme/tool#dev" }, tokens);
        Assert.Equal("fix the bug", rest);

        (tokens, rest) = ChatFormatting.SplitRepoTokens("fix the bug in a+b");
        Assert.Empty(tokens);
        Assert.Equal("fix the bug in a+b", rest);

        (tokens, rest) = ChatFormatting.SplitRepoTokens("fix +app later");   // not leading
        Assert.Empty(tokens);
        Assert.Equal("fix +app later", rest);

        (tokens, rest) = ChatFormatting.SplitRepoTokens("+ app");            // bare plus is prose
        Assert.Empty(tokens);
        Assert.Equal("+ app", rest);

        (tokens, rest) = ChatFormatting.SplitRepoTokens("+app");             // token without prompt
        Assert.Equal(new[] { "app" }, tokens);
        Assert.Equal("", rest);

        (tokens, rest) = ChatFormatting.SplitRepoTokens("+https://git.example.test/acme/app.git do it");
        Assert.Equal(new[] { "https://git.example.test/acme/app.git" }, tokens);
        Assert.Equal("do it", rest);
    }

    [Fact]
    public void TitleFromPrompt_UsesTheFirstLine_TrimmedAtAWordBoundary()
    {
        Assert.Equal("Fix the login bug", ChatFormatting.TitleFromPrompt("Fix the login bug\nSteps: …"));
        Assert.Equal("Chat session", ChatFormatting.TitleFromPrompt("   \n  "));

        var longTitle = ChatFormatting.TitleFromPrompt(
            "Investigate why the usage dashboard sometimes shows zero for subscription sessions");
        Assert.True(longTitle.Length <= 49);
        Assert.EndsWith("…", longTitle);
        Assert.DoesNotContain("  ", longTitle);

        var unbreakable = ChatFormatting.TitleFromPrompt(new string('x', 100));
        Assert.Equal(48, unbreakable.Length); // 47 chars + ellipsis
    }
}
