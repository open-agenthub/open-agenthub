using AgentHub.Api.Chat.Telegram;
using Xunit;

namespace AgentHub.Api.Tests;

public class TelegramUpdateTests
{
    [Fact]
    public void ParsesPlainMessage()
    {
        var u = TelegramUpdate.Parse("""
            {"update_id":1,"message":{"message_id":10,"chat":{"id":-100123,"type":"supergroup","is_forum":true},
             "from":{"id":42,"is_bot":false,"username":"maik"},"text":"hello","message_thread_id":77}}
            """);
        Assert.Equal(TelegramUpdateKind.Message, u!.Kind);
        Assert.Equal("-100123", u.ChatId);
        Assert.Equal("77", u.ThreadId);
        Assert.Equal("hello", u.Text);
        Assert.True(u.IsForumChat);
        Assert.Null(u.ReplyToMessageId);
        Assert.Equal("maik", u.FromUsername);
    }

    [Fact]
    public void ParsesReply()
    {
        var u = TelegramUpdate.Parse("""
            {"update_id":2,"message":{"message_id":11,"chat":{"id":5,"type":"private"},
             "from":{"id":42,"is_bot":false},"text":"yes","reply_to_message":{"message_id":9}}}
            """);
        Assert.Equal("9", u!.ReplyToMessageId);
        Assert.Null(u.ThreadId);
        Assert.False(u.IsForumChat);
    }

    [Fact]
    public void ParsesCallbackQuery()
    {
        var u = TelegramUpdate.Parse("""
            {"update_id":3,"callback_query":{"id":"cb1","from":{"id":42,"username":"maik"},
             "data":"perm:allow:abc","message":{"message_id":12,"chat":{"id":5,"type":"private"}}}}
            """);
        Assert.Equal(TelegramUpdateKind.Callback, u!.Kind);
        Assert.Equal("perm:allow:abc", u.CallbackData);
        Assert.Equal("cb1", u.CallbackId);
        Assert.Equal("12", u.MessageId);
        Assert.Equal("5", u.ChatId);
    }

    [Fact]
    public void IgnoresBotAndEmptyAndJunk()
    {
        Assert.Null(TelegramUpdate.Parse("""{"update_id":4,"message":{"message_id":1,"chat":{"id":5,"type":"private"},"from":{"id":1,"is_bot":true},"text":"x"}}"""));
        Assert.Null(TelegramUpdate.Parse("""{"update_id":5,"edited_message":{"message_id":2,"chat":{"id":5,"type":"private"},"text":"e"}}"""));
        Assert.Null(TelegramUpdate.Parse("""{"update_id":6,"message":{"message_id":3,"chat":{"id":5,"type":"private"},"from":{"id":42,"is_bot":false}}}"""));
        Assert.Null(TelegramUpdate.Parse("not json"));
    }

    [Fact]
    public void ForumTopicReply_ThreadWins_ServiceReplyIgnored()
    {
        // In forum topics every message carries message_thread_id AND reply_to_message
        // (pointing at the topic-creation service message) — thread id must win, reply ignored.
        var u = TelegramUpdate.Parse("""
            {"update_id":7,"message":{"message_id":13,"chat":{"id":-100123,"type":"supergroup","is_forum":true},
             "from":{"id":42,"is_bot":false},"text":"t","message_thread_id":77,"reply_to_message":{"message_id":77}}}
            """);
        Assert.Equal("77", u!.ThreadId);
        Assert.Null(u.ReplyToMessageId);
    }

    [Fact]
    public void ForumTopicReply_RealReplyKept()
    {
        // A real reply inside a topic points at a different message than the topic id.
        var u = TelegramUpdate.Parse("""
            {"update_id":8,"message":{"message_id":14,"chat":{"id":-100123,"type":"supergroup","is_forum":true},
             "from":{"id":42,"is_bot":false},"text":"t","message_thread_id":77,"reply_to_message":{"message_id":80}}}
            """);
        Assert.Equal("77", u!.ThreadId);
        Assert.Equal("80", u.ReplyToMessageId);
    }
}
