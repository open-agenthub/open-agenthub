using AgentHub.Api.Otel;
using AgentHub.Api.Usage;
using Xunit;

namespace AgentHub.Api.Tests;

public class ClaudePricingTests
{
    [Theory]
    [InlineData("claude-opus-4-8", 5, 25)]
    [InlineData("claude-opus-4-7", 5, 25)]
    [InlineData("claude-opus-4-1", 15, 75)]
    [InlineData("claude-fable-5", 10, 50)]
    [InlineData("claude-sonnet-5", 3, 15)]
    [InlineData("claude-sonnet-4-6", 3, 15)]
    [InlineData("claude-haiku-4-5-20251001", 1, 5)]
    [InlineData("claude-3-5-haiku-20241022", 0.8, 4)]
    [InlineData("", 3, 15)]           // unknown -> Sonnet fallback
    [InlineData(null, 3, 15)]
    [InlineData("gpt-oh-no", 3, 15)]
    public void Rates_MatchModelFamilies(string? model, double input, double output)
    {
        var (i, o) = ClaudePricing.Rates(model);
        Assert.Equal(input, i);
        Assert.Equal(output, o);
    }

    [Fact]
    public void EstimateUsd_AppliesCacheFactors()
    {
        // Opus 4.8: $5 in, $25 out, cache read 0.1x in, cache write 1.25x in.
        var usd = ClaudePricing.EstimateUsd("claude-opus-4-8",
            input: 1_000_000, output: 1_000_000, cacheRead: 1_000_000, cacheCreation: 1_000_000);
        Assert.Equal(5 + 25 + 0.5 + 6.25, usd, 6);
    }

    [Fact]
    public void EstimateUsd_Delta_SumsPerModelBuckets()
    {
        var delta = new SessionUsageDelta { SessionId = "s" };
        delta.ModelTokens["claude-opus-4-8"] = new ModelTokenUsage { InputTokens = 2_000_000 };  // $10
        delta.ModelTokens["claude-haiku-4-5"] = new ModelTokenUsage { OutputTokens = 1_000_000 }; // $5
        Assert.Equal(15, ClaudePricing.EstimateUsd(delta), 6);
    }

    [Fact]
    public void EstimateUsd_Delta_FallsBackToAggregates_WhenNoModelBuckets()
    {
        // Old pods without the model attribute still yield a non-zero estimate (Sonnet rates).
        var delta = new SessionUsageDelta
        {
            SessionId = "s",
            InputTokens = 1_000_000,
            OutputTokens = 1_000_000
        };
        Assert.Equal(3 + 15, ClaudePricing.EstimateUsd(delta), 6);
    }
}
