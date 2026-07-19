using System.Net.WebSockets;
using System.Text;
using AgentHub.Api.Permissions;
using AgentHub.Api.Persistence;
using AgentHub.Api.Services;

namespace AgentHub.Api.Chat.Signal;

/// <summary>
/// Signal receive loop: keeps a WebSocket to signal-cli-rest-api's /v1/receive endpoint
/// and routes inbound events — 👍/👎 reactions on permission prompts, a quoted "always"
/// reply for allowAlways, !sessions/!use/!status commands, and plain replies (typed
/// into the bound session's terminal). Only verified, opted-in senders are handled;
/// everything else is dropped. Community feature — no license required.
/// SECURITY: phone numbers are PII — logs carry owner/session ids only, never the number.
/// </summary>
public sealed class SignalReceiveService : BackgroundService
{
    /// <summary>A half-open TCP connection would hang ReceiveAsync forever (no keep-alive
    /// timeout on this runtime): no complete message within this window → reconnect.</summary>
    private static readonly TimeSpan ReceiveWatchdogTimeout = TimeSpan.FromMinutes(15);

    /// <summary>Anything bigger than this is not a chat event we care about — skip it.</summary>
    private const int MaxMessageBytes = 4 * 1024 * 1024;

    private readonly SignalOptions _opts;
    private readonly SignalClient _signal;
    private readonly ChatBindingStore _bindings;
    private readonly UserDirectory _users;
    private readonly PermissionStore _permissions;
    private readonly ISessionService _sessions;
    private readonly WorkingIndicator _indicator;
    private readonly int _agentPort;
    private readonly string _frontendOrigin;
    private readonly ILogger<SignalReceiveService> _log;

    private DateTime _lastPrune = DateTime.UtcNow;

    public SignalReceiveService(SignalOptions opts, SignalClient signal, ChatBindingStore bindings,
        UserDirectory users, PermissionStore permissions, ISessionService sessions,
        WorkingIndicator indicator, IConfiguration cfg, ILogger<SignalReceiveService> log)
    {
        _opts = opts; _signal = signal; _bindings = bindings; _users = users;
        _permissions = permissions; _sessions = sessions; _indicator = indicator;
        _agentPort = cfg.GetValue("AgentHub:AgentPort", 7681);
        _frontendOrigin = (cfg["FrontendOrigin"] ?? "").TrimEnd('/');
        _log = log;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_opts.CanRun)
        {
            _log.LogInformation("Signal receive loop not started (not configured / disabled).");
            return;
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            try { await RunOnceAsync(stoppingToken); }
            catch (OperationCanceledException) { break; }
            catch (Exception ex) { _log.LogWarning(ex, "Signal receive connection dropped; reconnecting in 5s"); }
            try { await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken); } catch { break; }
        }
    }

    private async Task RunOnceAsync(CancellationToken ct)
    {
        using var ws = new ClientWebSocket();
        await ws.ConnectAsync(_signal.GetReceiveUri(), ct);
        _log.LogInformation("Signal receive WebSocket connected.");

        // signal-cli keeps the socket open and pushes frames. A watchdog caps the wait
        // per message (deadline reset after every received one): when it fires we treat
        // it as a normal reconnect, not an error — idle chats reconnecting every 15 min
        // are harmless, dead sockets recover.
        var buf = new byte[64 * 1024];
        while (ws.State == WebSocketState.Open && !ct.IsCancellationRequested)
        {
            string text;
            bool closed;
            using (var watchdog = CancellationTokenSource.CreateLinkedTokenSource(ct))
            {
                watchdog.CancelAfter(ReceiveWatchdogTimeout);
                try { (text, closed) = await ReceiveFullAsync(ws, buf, watchdog.Token); }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                {
                    _log.LogDebug("Signal receive socket idle for {Timeout} — reconnecting", ReceiveWatchdogTimeout);
                    break;
                }
            }
            if (closed) break;
            if (text.Length == 0) continue;

            var envelope = SignalEnvelope.Parse(text);
            if (envelope is not null)
            {
                try { await DispatchAsync(envelope, ct); }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex) { _log.LogWarning(ex, "Handling a Signal message failed; continuing"); }
            }

            // Daily reply-routing cleanup (idempotent — harmless if Telegram prunes too).
            if (DateTime.UtcNow - _lastPrune > TimeSpan.FromHours(24))
            {
                await _bindings.PruneMessagesAsync(ct);
                _lastPrune = DateTime.UtcNow;
            }
        }
    }

    private async Task DispatchAsync(SignalEnvelope e, CancellationToken ct)
    {
        // The sender's number IS the identity: only verified, opted-in users may
        // interact — every other sender is ignored entirely (no reply, no log with PII).
        var user = await _users.GetBySignalNumberAsync(e.Sender, ct);
        if (user is not { SignalVerified: true, SignalEnabled: true }) return;

        if (e is { ReactionEmoji: not null, ReactionTargetTimestamp: not null })
        {
            await HandleReactionAsync(e, user, ct);
            return;
        }

        var text = e.Text?.Trim() ?? "";
        if (text.Length == 0) return;

        // Quoting a permission prompt with "always" grants allowAlways. Quotes of
        // anything else fall through to normal routing (plain input "always").
        if (text.Equals("always", StringComparison.OrdinalIgnoreCase) && e.QuotedTimestamp is not null)
        {
            var req = await _permissions.GetByPromptMessageAsync("signal", e.Sender, e.QuotedTimestamp, ct);
            if (req is not null && req.Owner == user.Owner)
            {
                var resolved = await _permissions.ResolveAsync(req.Id, "allowAlways", ct: ct);
                // A typed message deserves feedback either way (reactions stay silent).
                await _signal.SendAsync(e.Sender, resolved is not null
                    ? $"✅ Allowed — {resolved.Tool} (won't ask again this run)"
                    : "Already decided/expired — see the web terminal.", ct);
                return;
            }
        }

        var parts = text.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        switch (parts[0].ToLowerInvariant())
        {
            case "!sessions":
                await HandleSessionsAsync(e, user, ct);
                return;
            case "!use":
                await HandleUseAsync(e, user, parts.Length > 1 ? parts[1] : "", ct);
                return;
            case "!status":
                await HandleStatusAsync(e, user, ct);
                return;
            default:
                await HandlePlainAsync(e, user, text, ct);
                return;
        }
    }

    // ------------------------------------------------------------------ reactions

    /// <summary>👍/👎 on a permission prompt resolves it; other emoji (and reactions on
    /// non-prompt messages) are ignored. Already-decided/expired prompts stay silent —
    /// reaction spam must not produce message spam.</summary>
    private async Task HandleReactionAsync(SignalEnvelope e, AppUser user, CancellationToken ct)
    {
        var req = await _permissions.GetByPromptMessageAsync("signal", e.Sender, e.ReactionTargetTimestamp!, ct);
        if (req is null) return;
        if (req.Owner != user.Owner) return; // never decide another account's request

        var decision = ReactionDecision(e.ReactionEmoji!);
        if (decision is null) return;

        var resolved = await _permissions.ResolveAsync(req.Id, decision, ct: ct);
        if (resolved is null) return;

        var verb = decision == "deny" ? "⛔ Denied" : "✅ Allowed";
        await _signal.SendAsync(e.Sender, $"{verb} — {resolved.Tool}", ct);
    }

    /// <summary>Maps a reaction emoji to a permission decision: 👍 → "allow", 👎 → "deny",
    /// anything else → null. StartsWith tolerates skin-tone variants (👍🏽) — still the
    /// same gesture. Pure — exposed for tests.</summary>
    public static string? ReactionDecision(string emoji)
        => emoji.StartsWith("👍", StringComparison.Ordinal) ? "allow"
         : emoji.StartsWith("👎", StringComparison.Ordinal) ? "deny"
         : null;

    // ------------------------------------------------------------------- commands

    /// <summary>Lists this number's sessions with their live phase. Defensive owner
    /// filter: bindings must belong to the sender's account.</summary>
    private async Task HandleSessionsAsync(SignalEnvelope e, AppUser user, CancellationToken ct)
    {
        var bindings = (await _bindings.ListByChatAsync("signal", e.Sender, ct))
            .Where(b => b.Owner == user.Owner).ToList();
        if (bindings.Count == 0)
        {
            await _signal.SendAsync(e.Sender, "No sessions here yet.", ct);
            return;
        }

        var lines = new List<string>();
        foreach (var b in bindings)
        {
            var live = await _sessions.GetSessionAsync(b.Owner, b.SessionId, ct);
            lines.Add($"#{ChatFormatting.Tag(b.SessionId)} · {live?.Phase ?? "Unknown"}{(b.Active ? " · active" : "")}");
        }
        await _signal.SendAsync(e.Sender, string.Join("\n", lines), ct);
    }

    /// <summary>Points the chat's plain replies at the session matching the tag.</summary>
    private async Task HandleUseAsync(SignalEnvelope e, AppUser user, string tag, CancellationToken ct)
    {
        var matches = (await _bindings.ListByChatAsync("signal", e.Sender, ct))
            .Where(b => b.Owner == user.Owner && ChatFormatting.MatchesTag(tag, b.SessionId)).ToList();
        if (matches.Count == 0)
        {
            await _signal.SendAsync(e.Sender, "No session matches that tag.", ct);
            return;
        }
        if (matches.Count > 1)
        {
            await _signal.SendAsync(e.Sender, "Ambiguous — be more specific.", ct);
            return;
        }

        await _bindings.SetActiveAsync("signal", e.Sender, matches[0].SessionId, ct);
        await _signal.SendAsync(e.Sender, $"✅ Plain replies now go to #{ChatFormatting.Tag(matches[0].SessionId)}.", ct);
    }

    /// <summary>Answers with the target session's current state.</summary>
    private async Task HandleStatusAsync(SignalEnvelope e, AppUser user, CancellationToken ct)
    {
        var binding = await ResolveBindingAsync(e, ct);
        if (binding is null || binding.Owner != user.Owner)
        {
            await _signal.SendAsync(e.Sender, "No active session. !sessions lists them.", ct);
            return;
        }

        var live = await _sessions.GetSessionAsync(binding.Owner, binding.SessionId, ct);
        var pendingTool = await _permissions.GetPendingBySessionAsync(binding.SessionId, ct);
        var link = _frontendOrigin.Length == 0 ? null : $"{_frontendOrigin}/s/{binding.SessionId}";
        await _signal.SendAsync(e.Sender, ChatFormatting.StatusText(
            live?.Phase ?? "Unknown", live?.QuestionPending ?? false, pendingTool, link), ct);
    }

    // ---------------------------------------------------------------- plain input

    /// <summary>Plain reply: routed to a session and typed into its terminal.</summary>
    private async Task HandlePlainAsync(SignalEnvelope e, AppUser user, string text, CancellationToken ct)
    {
        var binding = await ResolveBindingAsync(e, ct);
        if (binding is null)
        {
            await _signal.SendAsync(e.Sender, "No active session. !sessions lists them.", ct);
            return;
        }

        // Ownership guard: bindings are created from the owner's own number, but a
        // stale row must never let one account drive another account's session.
        if (binding.Owner != user.Owner)
        {
            _log.LogWarning("Ignoring Signal reply for session {Id}: sender is not the session owner", binding.SessionId);
            return;
        }

        var info = await _sessions.GetSessionAsync(binding.Owner, binding.SessionId, ct);
        if (info?.PodIp is not { Length: > 0 } podIp || info.Phase != "Running")
        {
            await _signal.SendAsync(e.Sender, "⚠️ Session is not running — cannot deliver the reply.", ct);
            return;
        }

        await AgentTerminal.SendInputAsync(podIp, _agentPort, text, ct);
        _log.LogInformation("Delivered Signal reply to session {Id}", binding.SessionId);

        // Signal cannot edit messages, so the "working…" indicator is a static message:
        // delete the previous one (best-effort) and send a fresh frame — no animation
        // loop. Stop() is a cheap no-op here, just cross-platform defense.
        _indicator.Stop(binding.SessionId);
        if (binding.StatusRef is { } oldRef) await _signal.TryDeleteAsync(binding.ChatId, oldRef, ct);
        var ts = await _signal.SendAsync(e.Sender, WorkingIndicator.Frames[0], ct);
        if (ts is not null) await _bindings.SetStatusRefAsync("signal", binding.SessionId, ts, ct);
    }

    /// <summary>
    /// The session a message is meant for: (1) the quoted message's session, else
    /// (2) the chat's active session. A quote of an unknown message resolves to
    /// nothing on purpose — silently redirecting it to the active session would
    /// deliver the reply somewhere the user did not aim at.
    /// </summary>
    private async Task<ChatBinding?> ResolveBindingAsync(SignalEnvelope e, CancellationToken ct)
    {
        if (e.QuotedTimestamp is not null)
        {
            var sessionId = await _bindings.GetSessionByMessageAsync("signal", e.Sender, e.QuotedTimestamp, ct);
            return sessionId is null ? null : await _bindings.GetAsync("signal", sessionId, ct);
        }
        return await _bindings.GetActiveAsync("signal", e.Sender, ct);
    }

    private async Task<(string text, bool closed)> ReceiveFullAsync(ClientWebSocket ws, byte[] buf, CancellationToken ct)
    {
        using var ms = new MemoryStream();
        WebSocketReceiveResult msg;
        var oversized = false;
        do
        {
            msg = await ws.ReceiveAsync(buf, ct);
            if (msg.MessageType == WebSocketMessageType.Close) return ("", true);
            if (!oversized && ms.Length + msg.Count > MaxMessageBytes)
            {
                oversized = true; // drain the rest of the message, then drop it
                _log.LogWarning("Signal receive message exceeds {Max} bytes — skipping it", MaxMessageBytes);
            }
            if (!oversized) ms.Write(buf, 0, msg.Count);
        } while (!msg.EndOfMessage);
        return (oversized ? "" : Encoding.UTF8.GetString(ms.ToArray()), false);
    }
}
