using AgentHub.Api.Chat.Telegram;
using Xunit;

namespace AgentHub.Api.Tests;

public class TelegramAnswerTests
{
    [Fact]
    public void ShortMessage_SingleMessageWithLabelAndRawText()
    {
        var messages = TelegramNotifier.BuildAnswerMessages("Hello\nWorld");

        var single = Assert.Single(messages);
        Assert.Equal("💬 The agent says:\nHello\nWorld", single);
    }

    [Fact]
    public void SpecialCharacters_StayVerbatim()
    {
        // Telegram messages are sent without parse_mode — no HTML escaping, no quoting.
        var messages = TelegramNotifier.BuildAnswerMessages("a <b>& c > d");

        var single = Assert.Single(messages);
        Assert.Equal("💬 The agent says:\na <b>& c > d", single);
        Assert.DoesNotContain("&amp;", single);
        Assert.DoesNotContain("&lt;", single);
        Assert.DoesNotContain("\n> ", single);
    }

    [Fact]
    public void LongMultiLineMessage_SplitsWithContinuationLabels()
    {
        var message = string.Join("\n", Enumerable.Repeat(new string('x', 20), 300));

        var messages = TelegramNotifier.BuildAnswerMessages(message);

        Assert.True(messages.Count > 1);
        Assert.StartsWith("💬 The agent says:\n", messages[0]);
        for (var i = 1; i < messages.Count; i++)
            Assert.StartsWith($"… ({i + 1}/{messages.Count})\n", messages[i]);
        // Every content line survives untouched — no prefixes, no escaping, nothing lost.
        var contentLines = messages.SelectMany(m => m.Split('\n').Skip(1)).ToList();
        Assert.Equal(300, contentLines.Count);
        Assert.All(contentLines, l => Assert.Equal(new string('x', 20), l));
    }
}
