using AgentHub.Api.Ee.Slack;
using Xunit;

namespace AgentHub.Api.Tests;

public class SlackAnswerMessagesTests
{
    [Fact]
    public void ShortMessage_SingleMessageWithLabelAndBlockquote()
    {
        var messages = SlackNotifier.BuildAnswerMessages("Hello\nWorld");

        var single = Assert.Single(messages);
        var lines = single.Split('\n');
        Assert.Equal(":speech_balloon: *The agent says:*", lines[0]);
        Assert.All(lines.Skip(1), l => Assert.StartsWith("> ", l));
        Assert.Equal("> Hello", lines[1]);
        Assert.Equal("> World", lines[2]);
    }

    [Fact]
    public void LongMultiLineMessage_SplitsWithContinuationLabels()
    {
        var message = string.Join("\n", Enumerable.Repeat(new string('x', 20), 500));

        var messages = SlackNotifier.BuildAnswerMessages(message);

        Assert.True(messages.Count > 1);
        Assert.StartsWith(":speech_balloon: *The agent says:*\n", messages[0]);
        for (var i = 1; i < messages.Count; i++)
            Assert.StartsWith($"_… ({i + 1}/{messages.Count})_\n", messages[i]);
        // Every content line in every message is blockquoted, and no content is lost.
        var contentLines = messages.SelectMany(m => m.Split('\n').Skip(1)).ToList();
        Assert.All(contentLines, l => Assert.StartsWith("> ", l));
        Assert.Equal(500, contentLines.Count);
        Assert.All(contentLines, l => Assert.Equal("> " + new string('x', 20), l));
    }

    [Fact]
    public void SpecialCharacters_AreEscaped()
    {
        var messages = SlackNotifier.BuildAnswerMessages("a & b < c > d");

        var single = Assert.Single(messages);
        Assert.Contains("> a &amp; b &lt; c &gt; d", single);
    }

    [Fact]
    public void HardSplit_DoesNotCutEscapedEntities()
    {
        // A single overlong line of '&' forces hard splits. Because splitting happens
        // BEFORE escaping, no chunk may contain a cut entity like a dangling "&am".
        var messages = SlackNotifier.BuildAnswerMessages(new string('&', 5000));

        Assert.True(messages.Count > 1);
        foreach (var m in messages)
        {
            var content = string.Concat(m.Split('\n').Skip(1).Select(l => l[2..]));
            Assert.Equal("", content.Replace("&amp;", "")); // nothing but whole entities
        }
        // And nothing was lost: unescaping the concatenated content restores the input.
        var all = string.Concat(messages.Select(m => string.Concat(m.Split('\n').Skip(1).Select(l => l[2..]))));
        Assert.Equal(new string('&', 5000), all.Replace("&amp;", "&"));
    }
}
