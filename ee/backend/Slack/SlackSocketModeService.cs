// -----------------------------------------------------------------------------
// Open AgentHub Enterprise Edition — Slack integration.
// Part of the Enterprise Edition; NOT covered by the AGPL-3.0 license of the
// open-core. Source-available under the Open AgentHub Enterprise License
// (see ee/LICENSE); a valid subscription is required for production use.
// -----------------------------------------------------------------------------
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using AgentHub.Api.Licensing;
using AgentHub.Api.Permissions;
using AgentHub.Api.Services;

namespace AgentHub.Api.Ee.Slack;

/// <summary>
/// Slack Socket Mode listener: keeps an outbound WebSocket to Slack (no public
/// endpoint needed) and turns thread replies into session input. Runs only when a
/// bot + app token are configured and the enterprise license is valid.
/// </summary>
public sealed class SlackSocketModeService : BackgroundService
{
    private readonly SlackOptions _opts;
    private readonly IEnterpriseLicense _license;
    private readonly SlackClient _slack;
    private readonly SlackThreadStore _threads;
    private readonly AgentHub.Api.Permissions.PermissionStore _permissions;
    private readonly ISessionService _sessions;
    private readonly int _agentPort;
    private readonly ILogger<SlackSocketModeService> _log;

    public SlackSocketModeService(SlackOptions opts, IEnterpriseLicense license, SlackClient slack,
        SlackThreadStore threads, AgentHub.Api.Permissions.PermissionStore permissions,
        ISessionService sessions, IConfiguration cfg, ILogger<SlackSocketModeService> log)
    {
        _opts = opts; _license = license; _slack = slack; _threads = threads; _permissions = permissions;
        _sessions = sessions;
        _agentPort = cfg.GetValue("AgentHub:AgentPort", 7681);
        _log = log;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_opts.CanReceive)
        {
            _log.LogInformation("Slack Socket Mode not started (no app token / disabled).");
            return;
        }
        if (!_license.Enabled)
        {
            _log.LogWarning("Slack Socket Mode not started: no valid enterprise license.");
            return;
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            try { await RunOnceAsync(stoppingToken); }
            catch (OperationCanceledException) { break; }
            catch (Exception ex) { _log.LogWarning(ex, "Slack Socket Mode connection dropped; reconnecting in 5s"); }
            try { await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken); } catch { break; }
        }
    }

    private async Task RunOnceAsync(CancellationToken ct)
    {
        var url = await _slack.OpenSocketAsync(ct);
        if (url is null) { await Task.Delay(TimeSpan.FromSeconds(10), ct); return; }

        using var ws = new ClientWebSocket();
        await ws.ConnectAsync(new Uri(url), ct);
        _log.LogInformation("Slack Socket Mode connected.");

        var buf = new byte[64 * 1024];
        while (ws.State == WebSocketState.Open && !ct.IsCancellationRequested)
        {
            var (text, closed) = await ReceiveFullAsync(ws, buf, ct);
            if (closed) break;
            if (text.Length == 0) continue;

            using var doc = JsonDocument.Parse(text);
            var root = doc.RootElement;
            var type = root.TryGetProperty("type", out var t) ? t.GetString() : null;

            // Ack every envelope that carries one, so Slack does not retry.
            if (root.TryGetProperty("envelope_id", out var env) && env.GetString() is { } envelopeId)
                await SendAsync(ws, JsonSerializer.Serialize(new { envelope_id = envelopeId }), ct);

            if (type == "disconnect") break;               // Slack asks us to reconnect
            if (type == "interactive") { await HandleInteractiveAsync(root, ct); continue; }
            if (type != "events_api") continue;

            await HandleEventAsync(root, ct);
        }
    }

    private async Task HandleEventAsync(JsonElement root, CancellationToken ct)
    {
        if (!root.TryGetProperty("payload", out var payload) ||
            !payload.TryGetProperty("event", out var ev)) return;
        if (ev.TryGetProperty("type", out var et) && et.GetString() != "message") return;
        // Ignore edits/deletes and anything the bot itself posted (avoid loops).
        if (ev.TryGetProperty("subtype", out _)) return;
        if (ev.TryGetProperty("bot_id", out _)) return;
        if (!ev.TryGetProperty("thread_ts", out var tts) || tts.GetString() is not { } threadTs) return;
        var textReply = ev.TryGetProperty("text", out var txt) ? txt.GetString() ?? "" : "";
        if (string.IsNullOrWhiteSpace(textReply)) return;

        var thread = await _threads.GetByThreadTsAsync(threadTs, ct);
        if (thread is null) return;

        var info = await _sessions.GetSessionAsync(thread.Owner, thread.SessionId, ct);
        if (info?.PodIp is not { Length: > 0 } podIp || info.Phase != "Running")
        {
            await _slack.PostMessageAsync(thread.Channel, ":warning: Session is not running — cannot deliver the reply.", threadTs, ct);
            return;
        }

        await AgentTerminal.SendInputAsync(podIp, _agentPort, textReply, ct);
        _log.LogInformation("Delivered Slack reply to session {Id}", thread.SessionId);
    }

    // Handles a Block Kit button click on a permission prompt (perm:<decision>:<id>).
    private async Task HandleInteractiveAsync(JsonElement root, CancellationToken ct)
    {
        if (!root.TryGetProperty("payload", out var p)) return;
        if (!p.TryGetProperty("type", out var pt) || pt.GetString() != "block_actions") return;
        if (!p.TryGetProperty("actions", out var actions) || actions.GetArrayLength() == 0) return;

        var actionId = actions[0].TryGetProperty("action_id", out var a) ? a.GetString() ?? "" : "";
        if (!PermissionAction.TryParse(actionId, out var decision, out var reqId)) return;

        var user = p.TryGetProperty("user", out var u) && u.TryGetProperty("username", out var un)
            ? un.GetString() : (u.TryGetProperty("name", out var nm) ? nm.GetString() : null);

        var resolved = await _permissions.ResolveAsync(reqId, decision, ct);
        if (resolved is null)
        {
            // Already decided or expired (or unknown) — reflect the final state instead
            // of leaving the click apparently dead.
            var existing = await _permissions.GetAsync(reqId, ct);
            if (existing is { Channel: not null, MessageTs: not null })
                await _slack.UpdateMessageAsync(existing.Channel, existing.MessageTs,
                    existing.Decision == "expired"
                        ? $":hourglass: *Expired* — *{Escape(existing.Tool)}*. Please answer in the web terminal."
                        : $":information_source: Already decided ({existing.Decision}) — *{Escape(existing.Tool)}*.", null, ct);
            return;
        }

        // Update the original message to reflect the decision and drop the buttons.
        var channel = p.TryGetProperty("container", out var cont) && cont.TryGetProperty("channel_id", out var ch)
            ? ch.GetString() : resolved.Channel;
        var ts = cont.TryGetProperty("message_ts", out var mts) ? mts.GetString() : resolved.MessageTs;
        var verb = decision == "deny" ? ":no_entry: *Denied*" : ":white_check_mark: *Allowed*";
        var suffix = decision == "allowAlways" ? " (won't ask again this run)" : "";
        if (channel is not null && ts is not null)
            await _slack.UpdateMessageAsync(channel, ts,
                $"{verb} — *{resolved.Tool}*{suffix}" + (user is null ? "" : $" · by {user}"), null, ct);
    }

    private static string Escape(string s) => s.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;");

    private static async Task<(string text, bool closed)> ReceiveFullAsync(ClientWebSocket ws, byte[] buf, CancellationToken ct)
    {
        using var ms = new MemoryStream();
        WebSocketReceiveResult msg;
        do
        {
            msg = await ws.ReceiveAsync(buf, ct);
            if (msg.MessageType == WebSocketMessageType.Close) return ("", true);
            ms.Write(buf, 0, msg.Count);
        } while (!msg.EndOfMessage);
        return (Encoding.UTF8.GetString(ms.ToArray()), false);
    }

    private static Task SendAsync(ClientWebSocket ws, string json, CancellationToken ct)
        => ws.SendAsync(Encoding.UTF8.GetBytes(json), WebSocketMessageType.Text, true, ct);
}
