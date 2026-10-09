namespace AgentHub.Api.Services;

/// <summary>
/// One page of a session transcript, for a caller that follows a session it created by polling
/// instead of holding a websocket open.
/// </summary>
/// <param name="SessionId">The session this page belongs to.</param>
/// <param name="Phase">Pending | Running | Paused | Succeeded | Failed | Scheduled.</param>
/// <param name="Running">
/// False once the session has reached a phase it will not leave on its own, which is the signal to
/// stop polling. Reported alongside the text rather than left to the caller to infer from the
/// phase string, so a poller cannot loop for ever on a phase name it does not recognise.
/// </param>
/// <param name="Offset">Where this page starts. Clamped into the transcript, so it may differ from
/// what was asked for.</param>
/// <param name="NextOffset">Offset to pass to the next poll.</param>
/// <param name="Length">Total transcript length right now.</param>
/// <param name="Truncated">
/// True when more text was available than <c>maxChars</c> allowed. The next poll returns it; the
/// poller does not have to do anything except come back.
/// </param>
/// <param name="Text">The slice itself.</param>
public sealed record TranscriptPage(
    string SessionId,
    string Phase,
    bool Running,
    int Offset,
    int NextOffset,
    int Length,
    bool Truncated,
    string Text)
{
    public const int DefaultMaxChars = 100_000;

    /// <summary>
    /// The largest page. Matches the scrollback the session agent keeps, so a caller that polls
    /// after a long silence can still catch up in one request.
    /// </summary>
    public const int MaxChars = ScrollbackLimits.MaxChars;

    private static readonly string[] TerminalPhases =
        [SessionStatus.Succeeded, SessionStatus.Failed];

    /// <summary>
    /// Cuts the page a caller asked for out of the whole transcript.
    ///
    /// The offset is a position in the transcript *as it stands now*. The session agent keeps only
    /// the last 1 MB of scrollback and re-uploads that whole window, so a session that talks past
    /// 1 MB loses text from the front and every offset behind it shifts. There is no cheap way to
    /// detect that, so <see cref="Length"/> is always reported: a poller that sees it go down knows
    /// its cursor no longer means what it did. Clamping rather than failing on an out-of-range
    /// offset keeps such a poller producing empty pages instead of 400s until it catches up.
    /// </summary>
    public static TranscriptPage From(
        string sessionId, string phase, string transcript, int? offset, int? maxChars)
    {
        var length = transcript.Length;
        var start = Math.Clamp(offset ?? 0, 0, length);
        var limit = Math.Clamp(maxChars ?? DefaultMaxChars, 1, MaxChars);
        var available = length - start;
        var take = Math.Min(available, limit);

        return new TranscriptPage(
            SessionId: sessionId,
            Phase: phase,
            Running: !TerminalPhases.Contains(phase, StringComparer.OrdinalIgnoreCase),
            Offset: start,
            NextOffset: start + take,
            Length: length,
            Truncated: take < available,
            Text: transcript.Substring(start, take));
    }
}
