using AgentHub.Api.Services;
using Xunit;

namespace AgentHub.Api.Tests;

public class TranscriptReaderTests
{
    [Fact]
    public async Task UsesAndSanitizesPreferredTranscriptWithoutReadingFallback()
    {
        var fallbackReads = 0;
        var result = await TranscriptReader.ReadAsync(
            _ => Task.FromResult<string?>("\u001b[31mpreferred\u001b[0m"),
            _ =>
            {
                fallbackReads++;
                return Task.FromResult<string?>("fallback");
            });

        Assert.Equal("preferred", result);
        Assert.Equal(0, fallbackReads);
    }

    [Fact]
    public async Task UsesAndSanitizesFallbackWhenPreferredTranscriptIsEmpty()
    {
        var result = await TranscriptReader.ReadAsync(
            _ => Task.FromResult<string?>(string.Empty),
            _ => Task.FromResult<string?>("\u001b[31mfallback\u001b[0m"));

        Assert.Equal("fallback", result);
    }
}