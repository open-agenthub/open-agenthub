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
    /// One page for the web app's Transcript tab. Null when the session is unknown.
    /// </summary>
    public static async Task<ConversationPage?> PageAsync(
        ISessionService sessions, string owner, string id, string phase, int? offset, int? max, CancellationToken ct)
    {
        var entries = await sessions.GetConversationAsync(owner, id, ct);
        if (entries is { Count: > 0 }) return ConversationPage.FromEntries(id, phase, entries, offset, max);
        var text = await sessions.GetTranscriptAsync(owner, id, ct);
        return text is null ? null : ConversationPage.FromScrollback(id, phase, text, offset, max);
    }
}
