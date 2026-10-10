namespace AgentHub.Api.Services;

/// <summary>
/// The one size of a session's terminal scrollback, shared by everything that stores or pages it.
/// </summary>
public static class ScrollbackLimits
{
    /// <summary>
    /// 1 MB of characters. The session agent keeps exactly this much in memory
    /// (<c>MAX_BUFFER</c> in <c>agent-runtime/common/server.js</c>) and uploads that whole window,
    /// so this is the most the hub can ever receive. The Postgres copy used to be capped at 400 KB
    /// with a comment claiming it matched the agent — it did not, and a resume seeded from
    /// Postgres (no S3) silently lost the first 600 KB of a long session. One constant, referenced
    /// from the upload endpoint and from <see cref="TranscriptPage.MaxChars"/>, is what keeps the
    /// three from drifting apart again; the agent side cannot share a C# constant, so its value is
    /// pinned by a comment next to <c>MAX_BUFFER</c> and by <c>docs/transcripts.md</c>.
    /// </summary>
    public const int MaxChars = 1_000_000;
}
