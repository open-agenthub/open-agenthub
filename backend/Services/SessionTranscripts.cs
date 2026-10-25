namespace AgentHub.Api.Services;

/// <summary>
/// The one place that decides what "the transcript" is for a reader: the provider's own
/// conversation when the session agent uploaded one, the cleaned terminal scrollback otherwise.
/// Every surface that hands text to a person or an agent — the web app, shared links, the remote
/// API, the MCP tools — goes through here, so they cannot disagree about the source.
/// </summary>
public static class SessionTranscripts
{
    /// <summary>
    /// Plain text for the remote API and the MCP tools. Null when the session is unknown.
    /// </summary>
    public static async Task<string?> ReadableAsync(ISessionService sessions, string owner, string id, CancellationToken ct)
    {
        var entries = await sessions.GetConversationAsync(owner, id, ct);
        if (entries is { Count: > 0 }) return NativeTranscript.Render(entries);
        return await sessions.GetTranscriptAsync(owner, id, ct);
    }

    /// <summary>
    /// One page for the web app's Workspace. Every caller has resolved the session first, so a
    /// session that has written nothing yet gets an empty scrollback page here, not null: null
    /// became a 404 the Workspace could not tell from an unknown session, and a just-created
    /// session showed "Could not load the conversation" until its first upload (#241).
    /// <paramref name="live"/> reads a running session's transcript from its pod; when it has
    /// nothing, the page comes from the stored copy.
    /// </summary>
    public static async Task<ConversationPage> PageAsync(
        ISessionService sessions, string owner, string id, string phase, int? offset, int? max, CancellationToken ct,
        Func<CancellationToken, Task<IReadOnlyList<TranscriptEntry>?>>? live = null)
    {
        if (live is not null && await live(ct) is { Count: > 0 } current)
            return ConversationPage.FromEntries(id, phase, current, offset, max);
        var entries = await sessions.GetConversationAsync(owner, id, ct);
        if (entries is { Count: > 0 }) return ConversationPage.FromEntries(id, phase, entries, offset, max);
        var text = await sessions.GetTranscriptAsync(owner, id, ct);
        return ConversationPage.FromScrollback(id, phase, text ?? "", offset, max);
    }
}
