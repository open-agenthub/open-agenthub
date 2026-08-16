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
    /// Recognizes a "start a session" chat command ("/new &lt;prompt&gt;" or "!new &lt;prompt&gt;",
    /// case-insensitive) and extracts the prompt (may be empty — the caller answers with
    /// a usage hint then). Anything else is not a new-command.
    /// </summary>
    public static bool TryParseNewCommand(string text, out string prompt)
    {
        prompt = "";
        var trimmed = text.Trim();
        foreach (var prefix in new[] { "/new", "!new" })
        {
            if (!trimmed.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) continue;
            if (trimmed.Length > prefix.Length && !char.IsWhiteSpace(trimmed[prefix.Length])) continue;
            prompt = trimmed[prefix.Length..].Trim();
            return true;
        }
        return false;
    }

    /// <summary>
    /// Recognizes a "list git projects" chat command ("/repos [query]" or "!repos [query]",
    /// case-insensitive) and extracts the optional search query. Anything else is not a
    /// repos-command.
    /// </summary>
    public static bool TryParseReposCommand(string text, out string query)
    {
        query = "";
        var trimmed = text.Trim();
        foreach (var prefix in new[] { "/repos", "!repos", "/projects", "!projects" })
        {
            if (!trimmed.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) continue;
            if (trimmed.Length > prefix.Length && !char.IsWhiteSpace(trimmed[prefix.Length])) continue;
            query = trimmed[prefix.Length..].Trim();
            return true;
        }
        return false;
    }

    /// <summary>
    /// Splits leading "+repo" tokens off a /new prompt: "+name +group/name#branch fix it"
    /// → tokens ["name", "group/name#branch"], rest "fix it". Tokens are only recognized
    /// at the START of the prompt so a "+" later in normal prose is left alone. A bare
    /// "+" is not a token.
    /// </summary>
    public static (IReadOnlyList<string> RepoTokens, string Prompt) SplitRepoTokens(string prompt)
    {
        var tokens = new List<string>();
        var rest = prompt.Trim();
        while (rest.StartsWith('+') && rest.Length > 1 && !char.IsWhiteSpace(rest[1]))
        {
            var end = rest.IndexOfAny(new[] { ' ', '\t', '\n', '\r' });
            var token = end < 0 ? rest[1..] : rest[1..end];
            tokens.Add(token);
            rest = end < 0 ? "" : rest[(end + 1)..].TrimStart();
        }
        return (tokens, rest);
    }

    /// <summary>
    /// Derives a session title from a /new prompt: its first line, trimmed to ~48 chars
    /// at a word boundary. Falls back to a generic title for an all-whitespace prompt.
    /// </summary>
    public static string TitleFromPrompt(string prompt)
    {
        var line = prompt.Trim().Split('\n')[0].Trim();
        if (line.Length == 0) return "Chat session";
        if (line.Length <= 48) return line;
        var cut = line.LastIndexOf(' ', 47);
        return (cut > 20 ? line[..cut] : line[..47]).TrimEnd() + "…";
    }

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
