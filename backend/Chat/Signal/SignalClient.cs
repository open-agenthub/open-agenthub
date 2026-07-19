using System.Globalization;
using System.Net.Http.Json;
using System.Text.Json;

namespace AgentHub.Api.Chat.Signal;

/// <summary>Thin client for the signal-cli-rest-api service (bbernhard, json-rpc mode).
/// Outbound sends go over REST; the receive WebSocket is owned by the background service —
/// this client only derives the URI. SECURITY: recipient phone numbers are PII — never log them.</summary>
public sealed class SignalClient
{
    /// <summary>Signal handles long messages fine; this just guards against absurd payloads.</summary>
    private const int MaxMessageLength = 60000;

    private readonly IHttpClientFactory _http;
    private readonly SignalOptions _opts;
    private readonly ILogger<SignalClient> _log;

    public SignalClient(IHttpClientFactory http, SignalOptions opts, ILogger<SignalClient> log)
    { _http = http; _opts = opts; _log = log; }

    /// <summary>Sends a plain-text message and returns Signal's sent timestamp (the message's
    /// identifier, e.g. for later quotes/reactions/deletes) as an invariant string, or null on failure.</summary>
    public async Task<string?> SendAsync(string recipient, string text, CancellationToken ct)
    {
        if (text.Length > MaxMessageLength)
        {
            _log.LogWarning("Signal send text of {Length} chars exceeds the {Max} limit — truncating", text.Length, MaxMessageLength);
            text = text[..MaxMessageLength];
        }

        var body = new Dictionary<string, object?>
        {
            ["message"] = text,
            ["number"] = _opts.Number,
            ["recipients"] = new[] { recipient },
            ["text_mode"] = "normal"
        };

        try
        {
            var c = _http.CreateClient();
            using var resp = await c.PostAsJsonAsync($"{_opts.ApiUrl.TrimEnd('/')}/v2/send", body, ct);
            var content = await resp.Content.ReadAsStringAsync(ct);
            if (!resp.IsSuccessStatusCode)
            {
                // The body may contain error details but no secrets; keep it short.
                _log.LogWarning("Signal send failed: {Status} {Body}",
                    (int)resp.StatusCode, content.Length > 200 ? content[..200] : content);
                return null;
            }

            using var doc = JsonDocument.Parse(content);
            return doc.RootElement.TryGetProperty("timestamp", out var ts) && ts.TryGetInt64(out var timestamp)
                ? timestamp.ToString(CultureInfo.InvariantCulture) : null;
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Signal send error");
            return null;
        }
    }

    /// <summary>Remote-deletes one of OUR OWN sent messages (identified by its sent timestamp).
    /// signal-cli-rest-api ≥0.70 exposes this via POST /v2/send with remote_delete_timestamp, but the
    /// endpoint shape varies by version — operators can verify theirs at {ApiUrl}/v1/docs (swagger).
    /// Returns false when unsupported or failing; the caller treats false as "leave the message".</summary>
    public async Task<bool> TryDeleteAsync(string recipient, string timestampToDelete, CancellationToken ct)
    {
        if (!long.TryParse(timestampToDelete, NumberStyles.Integer, CultureInfo.InvariantCulture, out var ts))
            return false;

        var body = new Dictionary<string, object?>
        {
            ["number"] = _opts.Number,
            ["recipients"] = new[] { recipient },
            ["remote_delete_timestamp"] = ts
        };
        return await TryPostAsync("/v2/send", body, ct);
    }

    /// <summary>POSTs and maps success to true. 400/404 (endpoint shape not supported by this
    /// signal-cli-rest-api version) logs one debug line; everything else including exceptions → false.</summary>
    private async Task<bool> TryPostAsync(string path, object body, CancellationToken ct)
    {
        try
        {
            var c = _http.CreateClient();
            using var resp = await c.PostAsJsonAsync($"{_opts.ApiUrl.TrimEnd('/')}{path}", body, ct);
            if (resp.IsSuccessStatusCode) return true;
            if (resp.StatusCode is System.Net.HttpStatusCode.BadRequest or System.Net.HttpStatusCode.NotFound)
                _log.LogDebug("Signal {Path} not supported by this signal-cli-rest-api version ({Status})", path, (int)resp.StatusCode);
            return false;
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>The receive WebSocket URI, ws(s)://{host}/v1/receive/{number}, derived from ApiUrl.
    /// The background service owns the socket lifecycle.</summary>
    public Uri GetReceiveUri()
    {
        var builder = new UriBuilder(_opts.ApiUrl.TrimEnd('/'));
        builder.Scheme = builder.Scheme == "https" ? "wss" : "ws";
        builder.Path = $"/v1/receive/{Uri.EscapeDataString(_opts.Number)}";
        return builder.Uri;
    }
}
