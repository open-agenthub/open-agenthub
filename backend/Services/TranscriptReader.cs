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

    /// <summary>
    /// The scrollback as the session last uploaded it. Both copies are the same window: the pod
    /// writes S3 through a presigned url and then Postgres through its callback, on every tick.
    /// The url expires (12 hours) while the callback does not, so from then on S3 holds a frozen
    /// copy that the plain "S3 if present" rule kept serving — a session older than that never
    /// showed new output again (#111). Postgres is therefore taken whenever S3 does not contain
    /// it; S3 still wins while it does, which covers a Postgres copy trimmed or left empty.
    /// </summary>
    public static async Task<string?> ReadScrollbackAsync(
        Func<CancellationToken, Task<string?>> readObject,
        Func<CancellationToken, Task<string?>> readDatabase,
        CancellationToken ct = default)
    {
        var stored = await readObject(ct);
        var current = await readDatabase(ct);
        if (string.IsNullOrEmpty(current)) return stored;
        if (string.IsNullOrEmpty(stored) || !stored.Contains(current, StringComparison.Ordinal)) return current;
        return stored;
    }

    /// <summary>
    /// The provider's transcript (JSONL) from its two copies: the whole file in S3 and the last
    /// <see cref="ScrollbackLimits.MaxChars"/> in Postgres, written in that order on every change.
    /// The same expiring url as the scrollback (see <see cref="ReadScrollbackAsync"/>) freezes the
    /// S3 copy, so when S3 does not contain the Postgres tail, the tail is the newer one; the
    /// history before it is kept from S3, joined at the tail's first line. Without that overlap
    /// (S3 stopped before the tail starts) both are kept, with the turns between them missing,
    /// rather than dropping either the history or the recent turns.
    /// </summary>
    public static async Task<string?> ReadTranscriptAsync(
        Func<CancellationToken, Task<string?>> readObject,
        Func<CancellationToken, Task<string?>> readDatabase,
        CancellationToken ct = default)
    {
        var whole = await readObject(ct);
        var tail = await readDatabase(ct);
        if (string.IsNullOrEmpty(tail)) return whole;
        if (string.IsNullOrEmpty(whole)) return tail;
        if (whole.Contains(tail, StringComparison.Ordinal)) return whole;
        var newline = tail.IndexOf('\n');
        var firstLine = newline < 0 ? tail : tail[..(newline + 1)];
        var join = whole.IndexOf(firstLine, StringComparison.Ordinal);
        if (join >= 0) return string.Concat(whole.AsSpan(0, join), tail);
        return whole.EndsWith('\n') ? whole + tail : whole + "\n" + tail;
    }

    /// <summary>The same scrollback with terminal control sequences removed, for the user-facing API.</summary>
    public static async Task<string?> ReadAsync(
        Func<CancellationToken, Task<string?>> readPreferred,
        Func<CancellationToken, Task<string?>> readFallback,
        CancellationToken ct = default)
        => AgentTerminal.CleanTranscript(await ReadRawAsync(readPreferred, readFallback, ct));
}
