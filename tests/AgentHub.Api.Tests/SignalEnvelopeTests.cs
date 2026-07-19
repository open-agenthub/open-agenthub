using AgentHub.Api.Chat.Signal;
using Xunit;

namespace AgentHub.Api.Tests;

public class SignalEnvelopeTests
{
    [Fact]
    public void ParsesTextWithQuote()
    {
        var e = SignalEnvelope.Parse("""
            {"envelope":{"sourceNumber":"+491700000001","timestamp":1752900000001,
             "dataMessage":{"message":"yes do it","timestamp":1752900000001,
               "quote":{"id":1752899000000,"author":"+491520000000"}}},"account":"+491520000000"}
            """);
        Assert.Equal("+491700000001", e!.Sender);
        Assert.Equal("yes do it", e.Text);
        Assert.Equal("1752899000000", e.QuotedTimestamp);
        Assert.Equal("1752900000001", e.Timestamp);
        Assert.Null(e.ReactionEmoji);
    }

    [Fact]
    public void ParsesPlainText_NoQuote()
    {
        var e = SignalEnvelope.Parse("""
            {"envelope":{"sourceNumber":"+491700000001","timestamp":2,"dataMessage":{"message":"hi","timestamp":2}}}
            """);
        Assert.Equal("hi", e!.Text);
        Assert.Null(e.QuotedTimestamp);
    }

    [Fact]
    public void ParsesReaction()
    {
        var e = SignalEnvelope.Parse("""
            {"envelope":{"sourceNumber":"+491700000001","timestamp":1752900000002,
             "dataMessage":{"reaction":{"emoji":"👍","targetSentTimestamp":1752899000000,"isRemove":false}}}}
            """);
        Assert.Equal("👍", e!.ReactionEmoji);
        Assert.Equal("1752899000000", e.ReactionTargetTimestamp);
        Assert.Null(e.Text);
    }

    [Fact]
    public void FallsBackToSourceWhenNoSourceNumber()
    {
        var e = SignalEnvelope.Parse("""
            {"envelope":{"source":"+4917X","timestamp":3,"dataMessage":{"message":"x","timestamp":3}}}
            """);
        Assert.Equal("+4917X", e!.Sender);
    }

    [Fact]
    public void IgnoresRemovalsReceiptsAndJunk()
    {
        Assert.Null(SignalEnvelope.Parse("""{"envelope":{"sourceNumber":"+4917","timestamp":4,"dataMessage":{"reaction":{"emoji":"👍","targetSentTimestamp":1,"isRemove":true}}}}"""));
        Assert.Null(SignalEnvelope.Parse("""{"envelope":{"sourceNumber":"+4917","timestamp":5,"receiptMessage":{"isDelivery":true}}}"""));
        Assert.Null(SignalEnvelope.Parse("""{"envelope":{"sourceNumber":"+4917","timestamp":6,"typingMessage":{"action":"STARTED"}}}"""));
        Assert.Null(SignalEnvelope.Parse("""{"envelope":{"sourceNumber":"+4917","timestamp":7,"dataMessage":{"timestamp":7}}}"""));
        Assert.Null(SignalEnvelope.Parse("not json"));
    }

    [Fact]
    public void NonStringMessage_Null()
    {
        Assert.Null(SignalEnvelope.Parse("""{"envelope":{"sourceNumber":"+4917","timestamp":8,"dataMessage":{"message":123,"timestamp":8}}}"""));
    }
}
