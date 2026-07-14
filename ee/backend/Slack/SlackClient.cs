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
    public async Task<string?> PostMessageAsync(string channel, string text, string? threadTs, CancellationToken ct)
    {
        var c = Client(_opts.BotToken);
        var body = new Dictionary<string, object?>
        {
            ["channel"] = channel,
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

    /// <summary>Posts a message with Block Kit blocks (e.g. buttons); returns its ts.</summary>
    public async Task<string?> PostBlocksAsync(string channel, string fallbackText, object blocks, string? threadTs, CancellationToken ct)
    {
        var c = Client(_opts.BotToken);
        var body = new Dictionary<string, object?>
        {
            ["channel"] = channel, ["text"] = fallbackText, ["blocks"] = blocks, ["unfurl_links"] = false
        };
        if (threadTs is not null) body["thread_ts"] = threadTs;
        try
        {
            using var resp = await c.PostAsJsonAsync("https://slack.com/api/chat.postMessage", body, ct);
            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
            var root = doc.RootElement;
            if (root.TryGetProperty("ok", out var ok) && ok.GetBoolean()) return root.GetProperty("ts").GetString();
            _log.LogWarning("Slack postMessage(blocks) failed: {Err}", root.TryGetProperty("error", out var e) ? e.GetString() : "unknown");
            return null;
        }
        catch (Exception ex) { _log.LogWarning(ex, "Slack postMessage(blocks) error"); return null; }
    }

    /// <summary>Replaces a message's text/blocks (chat.update) — used to show a resolved decision.</summary>
    public async Task UpdateMessageAsync(string channel, string ts, string text, object? blocks, CancellationToken ct)
    {
        var c = Client(_opts.BotToken);
        var body = new Dictionary<string, object?> { ["channel"] = channel, ["ts"] = ts, ["text"] = text, ["blocks"] = blocks ?? Array.Empty<object>() };
        try { await c.PostAsJsonAsync("https://slack.com/api/chat.update", body, ct); }
        catch (Exception ex) { _log.LogWarning(ex, "Slack chat.update error"); }
    }

    /// <summary>Resolves a user's email to their DM channel id (lookupByEmail → conversations.open).</summary>
    public async Task<string?> OpenImByEmailAsync(string email, CancellationToken ct)
    {
        var c = Client(_opts.BotToken);
        try
        {
            using var lr = await c.GetAsync($"https://slack.com/api/users.lookupByEmail?email={Uri.EscapeDataString(email)}", ct);
            using var ldoc = JsonDocument.Parse(await lr.Content.ReadAsStringAsync(ct));
            if (!ldoc.RootElement.TryGetProperty("ok", out var lok) || !lok.GetBoolean())
            {
                _log.LogInformation("Slack lookupByEmail for {Email} failed: {Err}", email,
                    ldoc.RootElement.TryGetProperty("error", out var le) ? le.GetString() : "unknown");
                return null;
            }
            var userId = ldoc.RootElement.GetProperty("user").GetProperty("id").GetString();

            using var or = await c.PostAsJsonAsync("https://slack.com/api/conversations.open",
                new { users = userId }, ct);
            using var odoc = JsonDocument.Parse(await or.Content.ReadAsStringAsync(ct));
            if (odoc.RootElement.TryGetProperty("ok", out var ook) && ook.GetBoolean())
                return odoc.RootElement.GetProperty("channel").GetProperty("id").GetString();
            return null;
        }
        catch (Exception ex) { _log.LogWarning(ex, "Slack DM resolution for {Email} failed", email); return null; }
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
