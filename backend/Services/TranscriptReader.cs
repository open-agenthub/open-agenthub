namespace AgentHub.Api.Services;

/// <summary>Selects persisted terminal output: raw for the agent, plain text for people.</summary>
public static class TranscriptReader
{
    /// <summary>
    /// The scrollback exactly as the agent uploaded it, control sequences included. This is what
    /// a resuming pod seeds its buffer with: it replays the bytes into xterm, and stripped text
    /// (no carriage returns, no colours) renders there as a staircase of half-drawn screens. The
    /// first implementation handed the resume the cleaned text and then re-persisted that as the
    /// new raw scrollback — every resume degraded the history a little more.
    /// </summary>
    public static async Task<string?> ReadRawAsync(
        Func<CancellationToken, Task<string?>> readPreferred,
        Func<CancellationToken, Task<string?>> readFallback,
        CancellationToken ct = default)
    {
        var preferred = await readPreferred(ct);
        return !string.IsNullOrEmpty(preferred) ? preferred : await readFallback(ct);
    }

    /// <summary>The same scrollback with terminal control sequences removed, for the user-facing API.</summary>
    public static async Task<string?> ReadAsync(
        Func<CancellationToken, Task<string?>> readPreferred,
        Func<CancellationToken, Task<string?>> readFallback,
        CancellationToken ct = default)
        => AgentTerminal.CleanTranscript(await ReadRawAsync(readPreferred, readFallback, ct));
}
