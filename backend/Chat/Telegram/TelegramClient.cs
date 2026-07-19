using System.Globalization;
using System.Net.Http.Json;
using System.Text.Json;

namespace AgentHub.Api.Chat.Telegram;

/// <summary>Thin Telegram Bot API client (outbound messages + long-poll getUpdates).
/// SECURITY: the bot token is part of every request URL — never log URLs, only method names.</summary>
public sealed class TelegramClient
{
    /// <summary>Telegram caps forum topic names at 128 characters.</summary>
    private const int MaxTopicNameLength = 128;

    /// <summary>Telegram caps message text at 4096 characters.</summary>
    private const int MaxMessageLength = 4096;

    private readonly IHttpClientFactory _http;
    private readonly TelegramOptions _opts;
    private readonly ILogger<TelegramClient> _log;
    private string? _botUsername;

    public TelegramClient(IHttpClientFactory http, TelegramOptions opts, ILogger<TelegramClient> log)
    { _http = http; _opts = opts; _log = log; }

    private string Url(string method) => $"https://api.telegram.org/bot{_opts.BotToken}/{method}";

    /// <summary>POSTs a Bot API method. Returns (doc, null) when ok=true, (null, description) on an API
    /// error and (null, null) on transport/parse errors (both logged). Retries ONCE when the API answers
    /// 429 with parameters.retry_after (capped at 30s) — enough for multi-chunk sends and indicator
    /// edits without a token bucket.</summary>
    private async Task<(JsonDocument? Doc, string? Error)> PostAsync(string method, object body, CancellationToken ct)
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                var c = _http.CreateClient();
                using var resp = await c.PostAsJsonAsync(Url(method), body, ct);
                var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
                if (doc.RootElement.TryGetProperty("ok", out var ok) && ok.GetBoolean()) return (doc, null);

                var description = doc.RootElement.TryGetProperty("description", out var d) ? d.GetString() : null;
                if (attempt == 0 &&
                    doc.RootElement.TryGetProperty("parameters", out var p) &&
                    p.TryGetProperty("retry_after", out var ra) && ra.TryGetInt64(out var retryAfter))
                {
                    doc.Dispose();
                    var delay = TimeSpan.FromSeconds(Math.Clamp(retryAfter, 0, 30));
                    _log.LogDebug("Telegram {Method} rate-limited, retrying once in {Delay}s", method, delay.TotalSeconds);
                    await Task.Delay(delay, ct);
                    continue;
                }

                doc.Dispose();
                _log.LogWarning("Telegram {Method} failed: {Description}", method, description ?? "unknown");
                return (null, description);
            }
            catch (Exception ex) { _log.LogWarning(ex, "Telegram {Method} error", method); return (null, null); }
        }
    }

    /// <summary>Bot API ids are 64-bit ints; convert our string ids back for the wire (verbatim fallback).</summary>
    private static object AsId(string id)
        => long.TryParse(id, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) ? n : id;

    /// <summary>Sends a plain-text message (no parse_mode — avoids Markdown escaping bugs; emojis carry
    /// the formatting) and returns its message_id, or null on failure.</summary>
    public async Task<string?> SendMessageAsync(string chatId, string text, string? threadId, object? replyMarkup, CancellationToken ct)
    {
        if (text.Length > MaxMessageLength)
        {
            // Callers should split via ChatFormatting; degrade instead of silently getting null.
            _log.LogWarning("Telegram sendMessage text of {Length} chars exceeds the {Max} limit — truncating", text.Length, MaxMessageLength);
            text = text[..MaxMessageLength];
        }

        var body = new Dictionary<string, object?>
        {
            ["chat_id"] = AsId(chatId),
            ["text"] = text,
            ["disable_web_page_preview"] = true
        };
        if (threadId is not null) body["message_thread_id"] = AsId(threadId);
        if (replyMarkup is not null) body["reply_markup"] = replyMarkup;

        using var doc = (await PostAsync("sendMessage", body, ct)).Doc;
        if (doc is null) return null;
        return doc.RootElement.TryGetProperty("result", out var result) && result.TryGetProperty("message_id", out var mid)
            ? mid.GetInt64().ToString(CultureInfo.InvariantCulture) : null;
    }

    /// <summary>Replaces a message's text/markup. Returns false ONLY when the message is definitively
    /// gone/uneditable (the WorkingIndicator uses that to stop its loop); transient failures (429,
    /// network, unknown errors) and "not modified" no-ops return true.</summary>
    public async Task<bool> EditMessageTextAsync(string chatId, string messageId, string text, object? replyMarkup, CancellationToken ct)
    {
        var body = new Dictionary<string, object?>
        {
            ["chat_id"] = AsId(chatId),
            ["message_id"] = AsId(messageId),
            ["text"] = text
        };
        if (replyMarkup is not null) body["reply_markup"] = replyMarkup;

        var (doc, error) = await PostAsync("editMessageText", body, ct);
        if (doc is not null) { doc.Dispose(); return true; }
        if (error is null) return true; // transport/parse error — transient, keep going

        var e = error.ToLowerInvariant();
        if (e.Contains("message is not modified")) return true; // no-op edit — success
        return !(e.Contains("message to edit not found")
              || e.Contains("message can't be edited")
              || e.Contains("chat not found"));
    }

    /// <summary>Deletes a message — used to remove the transient "working…" status. Log-and-swallow.</summary>
    public async Task DeleteMessageAsync(string chatId, string messageId, CancellationToken ct)
    {
        var body = new Dictionary<string, object?> { ["chat_id"] = AsId(chatId), ["message_id"] = AsId(messageId) };
        using var _ = (await PostAsync("deleteMessage", body, ct)).Doc;
    }

    /// <summary>Creates a forum topic and returns its message_thread_id, or null on failure.</summary>
    public async Task<string?> CreateForumTopicAsync(string chatId, string name, CancellationToken ct)
    {
        if (name.Length > MaxTopicNameLength) name = name[..MaxTopicNameLength];
        var body = new Dictionary<string, object?> { ["chat_id"] = AsId(chatId), ["name"] = name };

        using var doc = (await PostAsync("createForumTopic", body, ct)).Doc;
        if (doc is null) return null;
        return doc.RootElement.TryGetProperty("result", out var result) && result.TryGetProperty("message_thread_id", out var tid)
            ? tid.GetInt64().ToString(CultureInfo.InvariantCulture) : null;
    }

    /// <summary>Acknowledges a button press (stops the client-side spinner). Log-and-swallow.</summary>
    public async Task AnswerCallbackAsync(string callbackId, string? text, CancellationToken ct)
    {
        var body = new Dictionary<string, object?> { ["callback_query_id"] = callbackId };
        if (text is not null) body["text"] = text;
        using var _ = (await PostAsync("answerCallbackQuery", body, ct)).Doc;
    }

    /// <summary>Long-polls getUpdates (timeout=50s) and returns the raw JSON of each update plus the next
    /// offset (max(update_id)+1; unchanged when empty or on errors — the caller loops with backoff).</summary>
    public async Task<(long nextOffset, IReadOnlyList<string> updates)> GetUpdatesAsync(long offset, CancellationToken ct)
    {
        try
        {
            var c = _http.CreateClient();
            // The server holds the request for up to 50s (timeout=50). Instead of raising the shared
            // HttpClient.Timeout we bound this request via a linked CTS at 70s (poll timeout + margin),
            // which stays below the factory client's 100s default and never affects other requests.
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(70));

            var allowedUpdates = Uri.EscapeDataString("""["message","callback_query"]""");
            var url = Url("getUpdates") + $"?offset={offset.ToString(CultureInfo.InvariantCulture)}&timeout=50&allowed_updates={allowedUpdates}";
            using var resp = await c.GetAsync(url, cts.Token);
            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(cts.Token));
            var root = doc.RootElement;
            if (!root.TryGetProperty("ok", out var ok) || !ok.GetBoolean())
            {
                _log.LogWarning("Telegram getUpdates failed: {Description}",
                    root.TryGetProperty("description", out var d) ? d.GetString() : "unknown");
                return (offset, Array.Empty<string>());
            }

            var updates = new List<string>();
            var nextOffset = offset;
            foreach (var el in root.GetProperty("result").EnumerateArray())
            {
                updates.Add(el.GetRawText());
                if (el.TryGetProperty("update_id", out var uid))
                    nextOffset = Math.Max(nextOffset, uid.GetInt64() + 1);
            }
            return (nextOffset, updates);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; } // shutdown — let the poll loop end
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Telegram getUpdates error");
            return (offset, Array.Empty<string>());
        }
    }

    /// <summary>Returns the bot's username (getMe), cached after the first success — used for t.me deep links.</summary>
    public async Task<string?> GetBotUsernameAsync(CancellationToken ct)
    {
        if (_botUsername is not null) return _botUsername;

        using var doc = (await PostAsync("getMe", new { }, ct)).Doc;
        if (doc is null) return null;
        if (doc.RootElement.TryGetProperty("result", out var result) &&
            result.TryGetProperty("username", out var un) && un.GetString() is { Length: > 0 } username)
        {
            _botUsername = username;
            return username;
        }
        return null;
    }
}
