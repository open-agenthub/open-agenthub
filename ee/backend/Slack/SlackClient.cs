// -----------------------------------------------------------------------------
// Open AgentHub Enterprise Edition — Slack integration.
// Part of the Enterprise Edition; NOT covered by the AGPL-3.0 license of the
// open-core. Source-available under the Open AgentHub Enterprise License
// (see ee/LICENSE); a valid subscription is required for production use.
// -----------------------------------------------------------------------------
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

namespace AgentHub.Api.Ee.Slack;

/// <summary>Thin Slack Web API client (chat.postMessage + Socket Mode connection).</summary>
public sealed class SlackClient
{
    private readonly IHttpClientFactory _http;
    private readonly SlackOptions _opts;
    private readonly ILogger<SlackClient> _log;

    public SlackClient(IHttpClientFactory http, SlackOptions opts, ILogger<SlackClient> log)
    { _http = http; _opts = opts; _log = log; }

    private HttpClient Client(string token)
    {
        var c = _http.CreateClient();
        c.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return c;
    }

    /// <summary>Posts a message (optionally into a thread) and returns its ts, or null on failure.</summary>
    public async Task<string?> PostMessageAsync(string text, string? threadTs, CancellationToken ct)
    {
        var c = Client(_opts.BotToken);
        var body = new Dictionary<string, object?>
        {
            ["channel"] = _opts.Channel,
            ["text"] = text,
            ["unfurl_links"] = false,
            ["unfurl_media"] = false
        };
        if (threadTs is not null) body["thread_ts"] = threadTs;

        try
        {
            using var resp = await c.PostAsJsonAsync("https://slack.com/api/chat.postMessage", body, ct);
            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
            var root = doc.RootElement;
            if (root.TryGetProperty("ok", out var ok) && ok.GetBoolean())
                return root.GetProperty("ts").GetString();
            _log.LogWarning("Slack chat.postMessage failed: {Err}", root.TryGetProperty("error", out var e) ? e.GetString() : "unknown");
            return null;
        }
        catch (Exception ex) { _log.LogWarning(ex, "Slack chat.postMessage error"); return null; }
    }

    /// <summary>Opens a Socket Mode connection and returns the wss URL to connect to.</summary>
    public async Task<string?> OpenSocketAsync(CancellationToken ct)
    {
        var c = Client(_opts.AppToken);
        using var resp = await c.PostAsync("https://slack.com/api/apps.connections.open", null, ct);
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
        var root = doc.RootElement;
        if (root.TryGetProperty("ok", out var ok) && ok.GetBoolean())
            return root.GetProperty("url").GetString();
        _log.LogWarning("Slack apps.connections.open failed: {Err}", root.TryGetProperty("error", out var e) ? e.GetString() : "unknown");
        return null;
    }
}
