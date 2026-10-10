namespace AgentHub.Api.Services;

/// <summary>
/// One page of a session's conversation for the web app, which polls it while the session runs.
/// Two shapes behind one cursor: <see cref="Source"/> <c>native</c> pages <see cref="Entries"/>
/// read from the provider's own transcript, and the offsets count entries; <c>scrollback</c> is
/// the fallback for sessions without one, pages <see cref="Text"/> like <see cref="TranscriptPage"/>
/// does, and the offsets count characters. A caller treats <see cref="NextOffset"/> as opaque and
/// hands it back; it only has to look at <see cref="Source"/> to know what to render.
/// </summary>
/// <param name="Length">Total entries (or characters) right now; a value that goes down means the
/// cursor is stale and the caller reloads from zero — the same contract as <see cref="TranscriptPage"/>.</param>
public sealed record ConversationPage(
    string SessionId,
    string Phase,
    bool Running,
    string Source,
    int Offset,
    int NextOffset,
    int Length,
    bool Truncated,
    IReadOnlyList<TranscriptEntry> Entries,
    string Text)
{
    public const string NativeSource = "native";
    public const string ScrollbackSource = "scrollback";
    public const int DefaultMaxEntries = 500;
    public const int MaxEntries = 5_000;

    public static ConversationPage FromEntries(
        string sessionId, string phase, IReadOnlyList<TranscriptEntry> entries, int? offset, int? max)
    {
        var length = entries.Count;
        var start = Math.Clamp(offset ?? 0, 0, length);
        var limit = Math.Clamp(max ?? DefaultMaxEntries, 1, MaxEntries);
        var take = Math.Min(length - start, limit);
        return new ConversationPage(sessionId, phase, TranscriptPage.StillRunning(phase), NativeSource,
            start, start + take, length, take < length - start,
            entries.Skip(start).Take(take).ToList(), "");
    }

    public static ConversationPage FromScrollback(string sessionId, string phase, string text, int? offset, int? maxChars)
    {
        var page = TranscriptPage.From(sessionId, phase, text, offset, maxChars);
        return new ConversationPage(sessionId, phase, page.Running, ScrollbackSource,
            page.Offset, page.NextOffset, page.Length, page.Truncated, [], page.Text);
    }
}
