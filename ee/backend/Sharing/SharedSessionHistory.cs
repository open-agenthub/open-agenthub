// -----------------------------------------------------------------------------
// Open AgentHub Enterprise Edition — Session sharing.
// Part of the Enterprise Edition; NOT covered by the AGPL-3.0 license of the
// open-core. Source-available under the Open AgentHub Enterprise License
// (see ee/LICENSE); a valid subscription is required for production use.
// -----------------------------------------------------------------------------
using System.Text.Json;
using AgentHub.Api.Persistence;
using AgentHub.Api.Services;

namespace AgentHub.Api.Ee.Sharing;

/// <summary>
/// The conversation history a person a session is shared with may read (#427): named shares,
/// inherited and project access, and links, every role. One service so the shared web view, the
/// remote API and the MCP read tools cannot disagree about source, bounds or what is withheld.
/// </summary>
public interface ISharedSessionHistory
{
    /// <summary>
    /// One page of the conversation. <paramref name="access"/> is what
    /// <see cref="ISessionAccessService"/> resolved for this request; the service does not resolve
    /// again, so a revoked share or an expired link is refused by that call, made per request.
    /// <paramref name="offset"/>: null is the start, -1 the most recent window, anything else a
    /// previous page's <see cref="ConversationPage.NextOffset"/>. <paramref name="max"/> counts
    /// entries for a native page and characters for a scrollback page.
    /// </summary>
    Task<ConversationPage> ReadAsync(SessionAccessResult access, int? offset, int? max, CancellationToken ct = default);
}

public sealed class SharedSessionHistory(ISessionService sessions, ILiveTranscriptReader? live = null)
    : ISharedSessionHistory
{
    public const int MaxEntries = ConversationPage.DefaultMaxEntries;
    public const int MaxScrollbackChars = TranscriptPage.DefaultMaxChars;
    // Entries are capped per tool result but not per assistant turn; 500 long turns would hand a
    // link holder tens of megabytes in one response. The page stops early instead and says so.
    public const int MaxPageChars = 1_000_000;
    public const string Redacted = "[redacted]";

    public async Task<ConversationPage> ReadAsync(
        SessionAccessResult access, int? offset, int? max, CancellationToken ct = default)
    {
        var record = access.Session;
        // The stored status decides whether the pod is asked at all: a stopped session costs no
        // Kubernetes read per poll. A running one is read from its pod, as the owner's Workspace
        // is — the stored copy keeps only the last ScrollbackLimits.MaxChars in Postgres, and
        // once the presigned upload url has expired that tail is all a reader got: a few hours of
        // a long session (#427).
        Func<CancellationToken, Task<IReadOnlyList<TranscriptEntry>?>>? livePage = null;
        var phase = record.Status;
        if (live is not null && record.Status == SessionStatus.Running
            && await sessions.GetSessionAsync(record.Owner, record.Id, ct) is { } info)
        {
            livePage = token => live.ReadAsync(info, token);
        }

        // One max for both shapes, as the owner's page has: entries are trimmed to MaxEntries
        // below, scrollback characters are bounded here.
        var limit = max is null ? (int?)null : Math.Clamp(max.Value, 1, MaxScrollbackChars);
        var page = await SessionTranscripts.PageAsync(sessions, record.Owner, record.Id, phase, offset, limit, ct, livePage);
        return Sanitize(page, record, offset == -1);
    }

    /// <summary>
    /// Withholds what the shared status withholds. Session secrets are masked by the pod before
    /// any transcript leaves it; the callback token and the inline MCP configuration are not
    /// secrets in that sense, so the pod leaves them alone — yet an agent that prints its
    /// environment or its MCP config would hand a link holder the token that acts as the session
    /// (agent_send, secret_request) and the owner's MCP credentials.
    /// </summary>
    public static ConversationPage Sanitize(ConversationPage page, SessionRecord record, bool recentWindow = false)
    {
        var needles = Needles(record);
        string Clean(string text) => Mask(text, needles);

        if (page.Source == ConversationPage.ScrollbackSource)
            return page with { Text = Clean(page.Text) };

        // A recent-window request (-1) keeps the newest entries of the page; any other keeps the
        // oldest, so following nextOffset pages forward without gaps either way.
        var source = recentWindow ? page.Entries.Reverse() : page.Entries;
        var kept = new List<TranscriptEntry>(Math.Min(page.Entries.Count, MaxEntries));
        var chars = 0;
        foreach (var entry in source)
        {
            var size = entry.Text.Length + (entry.Tool?.Length ?? 0);
            // Always at least one entry, so a single oversized turn cannot stall the cursor.
            if (kept.Count == MaxEntries || (kept.Count > 0 && chars + size > MaxPageChars)) break;
            chars += size;
            kept.Add(entry with { Text = Clean(entry.Text), Tool = entry.Tool is null ? null : Clean(entry.Tool) });
        }
        if (recentWindow) kept.Reverse();
        if (kept.Count == page.Entries.Count) return page with { Entries = kept };
        return recentWindow
            ? page with { Entries = kept, Offset = page.NextOffset - kept.Count }
            : page with { Entries = kept, NextOffset = page.Offset + kept.Count, Truncated = true };
    }

    /// <summary>The same withholding for the plain scrollback text the replay endpoints return.</summary>
    public static string SanitizeText(string text, SessionRecord record) => Mask(text, Needles(record));

    private static IReadOnlyList<string> Needles(SessionRecord record)
    {
        var values = new List<string>();
        if (!string.IsNullOrEmpty(record.CallbackToken)) values.Add(record.CallbackToken);
        if (!string.IsNullOrWhiteSpace(record.McpConfigJson))
        {
            try
            {
                using var document = JsonDocument.Parse(record.McpConfigJson);
                CollectCredentials(document.RootElement, false, values);
            }
            catch (JsonException)
            {
                // Unparseable configuration never reached a pod as MCP servers; nothing to mask.
            }
        }
        return values
            .Where(v => v.Trim().Length >= SecretMask.MinLength)
            .Distinct(StringComparer.Ordinal)
            .OrderByDescending(v => v.Length)
            .ToList();
    }

    // Header and env values are where an MCP config carries credentials; urls carry them in a
    // query string often enough that their whole value is withheld too. Names, commands and
    // arguments stay readable: the conversation would otherwise lose every tool name.
    private static void CollectCredentials(JsonElement element, bool credential, List<string> values)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var property in element.EnumerateObject())
                {
                    var name = property.Name;
                    var holds = credential
                        || name.Equals("headers", StringComparison.OrdinalIgnoreCase)
                        || name.Equals("env", StringComparison.OrdinalIgnoreCase)
                        || name.Equals("url", StringComparison.OrdinalIgnoreCase);
                    CollectCredentials(property.Value, holds, values);
                }
                break;
            case JsonValueKind.Array:
                foreach (var item in element.EnumerateArray()) CollectCredentials(item, credential, values);
                break;
            case JsonValueKind.String when credential:
                var value = element.GetString() ?? "";
                values.Add(value);
                // "Bearer abc…": the token alone is what reappears in a curl the agent prints.
                var space = value.LastIndexOf(' ');
                if (space > 0) values.Add(value[(space + 1)..]);
                break;
        }
    }

    private static string Mask(string text, IReadOnlyList<string> needles)
    {
        if (string.IsNullOrEmpty(text)) return text;
        foreach (var needle in needles)
            text = text.Replace(needle, Redacted, StringComparison.Ordinal);
        return text;
    }
}
