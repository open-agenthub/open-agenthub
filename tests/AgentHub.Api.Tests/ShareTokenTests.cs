using AgentHub.Api.Ee.Sharing;
using Xunit;

namespace AgentHub.Api.Tests;

public class ShareTokenTests
{
    [Fact]
    public void GeneratedToken_RoundTripsThroughHashWithoutStoringPlaintext()
    {
        var issued = ShareTokens.Issue();

        Assert.Equal(43, issued.Token.Length);
        Assert.DoesNotContain("=", issued.Token, StringComparison.Ordinal);
        Assert.DoesNotContain("+", issued.Token, StringComparison.Ordinal);
        Assert.DoesNotContain("/", issued.Token, StringComparison.Ordinal);
        Assert.Equal(32, issued.Hash.Length);
        Assert.True(ShareTokens.Matches(issued.Token, issued.Hash));
        Assert.False(ShareTokens.Matches(issued.Token + "x", issued.Hash));
    }

    [Fact]
    public void IssuedTokens_AreUnique()
    {
        var tokens = Enumerable.Range(0, 32)
            .Select(_ => ShareTokens.Issue().Token)
            .ToHashSet(StringComparer.Ordinal);

        Assert.Equal(32, tokens.Count);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not base64!")]
    [InlineData("abc")]
    [InlineData("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa")]
    public void Matches_RejectsMalformedTokens(string token)
    {
        Assert.False(ShareTokens.Matches(token, new byte[32]));
        Assert.False(ShareTokens.TryHash(token, out _));
    }

    [Fact]
    public void Matches_RejectsUnexpectedHashLength()
    {
        var issued = ShareTokens.Issue();

        Assert.False(ShareTokens.Matches(issued.Token, new byte[31]));
        Assert.False(ShareTokens.Matches(issued.Token, new byte[33]));
    }
}
