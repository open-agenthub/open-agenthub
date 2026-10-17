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
/// <summary>
/// The freshest-copy rules behind #111: an S3 copy frozen by an expired presigned url must not
/// hide the Postgres copy the pod keeps writing.
/// </summary>
public class FreshestCopyTests
{
    private static Func<CancellationToken, Task<string?>> Read(string? value) => _ => Task.FromResult(value);
    private static string Line(int n) => $"{{\"type\":\"user\",\"message\":{{\"role\":\"user\",\"content\":\"turn {n}\"}}}}\n";

    [Fact]
    public async Task Transcript_UsesTheWholeFileWhileItContainsTheTail()
    {
        var whole = Line(1) + Line(2) + Line(3);
        Assert.Equal(whole, await TranscriptReader.ReadTranscriptAsync(Read(whole), Read(Line(2) + Line(3))));
    }

    [Fact]
    public async Task Transcript_JoinsAFrozenFileToTheNewerTailAtItsFirstLine()
    {
        // S3 stopped after turn 3; Postgres holds the capped tail from turn 2 to turn 5.
        var frozen = Line(1) + Line(2) + Line(3);
        var tail = Line(2) + Line(3) + Line(4) + Line(5);

        var text = await TranscriptReader.ReadTranscriptAsync(Read(frozen), Read(tail));

        Assert.Equal(Line(1) + tail, text);
        Assert.Equal(5, NativeTranscript.Parse(text).Count);
    }

    [Fact]
    public async Task Transcript_KeepsBothWhenTheTailStartsAfterTheFrozenFileEnds()
    {
        var text = await TranscriptReader.ReadTranscriptAsync(Read(Line(1)), Read(Line(7) + Line(8)));
        Assert.Equal(Line(1) + Line(7) + Line(8), text);
    }

    [Fact]
    public async Task Transcript_FallsBackToWhicheverCopyExists()
    {
        Assert.Equal(Line(1), await TranscriptReader.ReadTranscriptAsync(Read(null), Read(Line(1))));
        Assert.Equal(Line(1), await TranscriptReader.ReadTranscriptAsync(Read(Line(1)), Read("")));
        Assert.Null(await TranscriptReader.ReadTranscriptAsync(Read(null), Read(null)));
    }

    [Fact]
    public async Task Scrollback_TakesThePostgresCopyWhenTheObjectDoesNotContainIt()
    {
        Assert.Equal("old\r\nnew\r\n", await TranscriptReader.ReadScrollbackAsync(Read("old\r\n"), Read("old\r\nnew\r\n")));
    }

    [Fact]
    public async Task Scrollback_KeepsTheObjectWhileItContainsThePostgresCopy()
    {
        // A trimmed or partial database copy is not newer, just shorter.
        Assert.Equal("a\r\nb\r\n", await TranscriptReader.ReadScrollbackAsync(Read("a\r\nb\r\n"), Read("b\r\n")));
        Assert.Equal("a\r\n", await TranscriptReader.ReadScrollbackAsync(Read("a\r\n"), Read(null)));
        Assert.Equal("b\r\n", await TranscriptReader.ReadScrollbackAsync(Read(null), Read("b\r\n")));
    }
}
