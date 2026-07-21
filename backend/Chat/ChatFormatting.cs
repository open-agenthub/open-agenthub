namespace AgentHub.Api.Chat;

/// <summary>Platform-neutral chat text helpers: session tags, headers, splitting.</summary>
public static class ChatFormatting
{
    /// <summary>Short session tag shown in chat (first 4 chars of the session id).</summary>
    public static string Tag(string sessionId) => sessionId.Length <= 4 ? sessionId : sessionId[..4];

    /// <summary>True when <paramref name="tag"/> is a non-empty prefix of the session id.</summary>
    public static bool MatchesTag(string tag, string sessionId)
        => tag.Length > 0 && sessionId.StartsWith(tag, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Finds the item whose session id matches <paramref name="tag"/> (see MatchesTag).
    /// Match is only non-null when it is UNIQUE; Count carries the number of matches so
    /// callers can tell "no such tag" (0) from "ambiguous — be more specific" (&gt;1).
    /// </summary>
    public static (T? Match, int Count) FindByTag<T>(string tag, IEnumerable<T> items, Func<T, string> sessionId)
    {
        T? match = default;
        var count = 0;
        foreach (var item in items)
        {
            if (!MatchesTag(tag, sessionId(item))) continue;
            count++;
            match = item;
        }
        return (count == 1 ? match : default, count);
    }

    public static string Header(string sessionId, string title) => $"🤖 #{Tag(sessionId)} · {title}";

    /// <summary>
    /// Splits text into chunks of at most maxLen, preferring line boundaries; a single
    /// line longer than maxLen is hard-split (never inside a surrogate pair). Blank lines
    /// at chunk boundaries and leading/trailing newlines may be dropped; content lines
    /// are preserved in order.
    /// </summary>
    public static IReadOnlyList<string> Split(string text, int maxLen)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maxLen, 1);
        var chunks = new List<string>();
        if (string.IsNullOrEmpty(text)) return chunks;
        var current = new System.Text.StringBuilder();
        foreach (var rawLine in text.Split('\n'))
        {
            var line = rawLine;
            while (line.Length > maxLen) // hard split an overlong line
            {
                if (current.Length > 0) { chunks.Add(current.ToString()); current.Clear(); }
                var cut = maxLen;
                if (cut > 1 && char.IsHighSurrogate(line[cut - 1])) cut--; // keep surrogate pairs intact
                chunks.Add(line[..cut]);
                line = line[cut..];
            }
            if (current.Length + line.Length + 1 > maxLen && current.Length > 0)
            { chunks.Add(current.ToString()); current.Clear(); }
            if (current.Length > 0) current.Append('\n');
            current.Append(line);
        }
        if (current.Length > 0) chunks.Add(current.ToString());
        return chunks;
    }

    /// <summary>
    /// Builds the labeled chat messages for one agent answer (pure — exposed for tests):
    /// the text split into maxLen chunks, the first prefixed with a "The agent says"
    /// label, continuations with a counter. The text itself stays verbatim: no escaping,
    /// no quote prefixes — just a label line per chunk. Shared by the Telegram and
    /// Signal notifiers (both send plain text).
    /// </summary>
    public static IReadOnlyList<string> BuildAnswerMessages(string message, int maxLen = 4000)
    {
        var chunks = Split(message.Trim(), maxLen);
        return chunks.Select((c, i) =>
        {
            var label = i == 0 ? "💬 The agent says:\n" : $"… ({i + 1}/{chunks.Count})\n";
            return label + c;
        }).ToList();
    }

    public static string StatusText(string phase, bool questionPending, string? pendingTool, string? link)
    {
        var lines = new List<string> { $"Status: {phase}" };
        if (questionPending) lines.Add("💬 Waiting for your reply.");
        if (pendingTool is not null) lines.Add($"🔒 Permission pending: {pendingTool}");
        if (!questionPending && pendingTool is null && phase == "Running") lines.Add("⏳ Claude is working.");
        if (!string.IsNullOrEmpty(link)) lines.Add(link);
        return string.Join("\n", lines);
    }
}
