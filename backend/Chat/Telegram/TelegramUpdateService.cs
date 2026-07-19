using System.Collections.Concurrent;
using AgentHub.Api.Permissions;
using AgentHub.Api.Persistence;
using AgentHub.Api.Services;

namespace AgentHub.Api.Chat.Telegram;

/// <summary>
/// Telegram long-poll router: getUpdates in a loop, dispatching permission button
/// callbacks, link/sessions/use/status commands and plain replies (typed into the
/// bound session's terminal). Runs only when a bot token is configured. Community
/// feature — no license required.
/// </summary>
public sealed class TelegramUpdateService : BackgroundService
{
    private const int MaxLinkAttempts = 5;
    private static readonly TimeSpan LinkThrottleWindow = TimeSpan.FromMinutes(10);

    private readonly TelegramOptions _opts;
    private readonly TelegramClient _tg;
    private readonly ChatBindingStore _bindings;
    private readonly ChatLinkCodeStore _codes;
    private readonly UserDirectory _users;
    private readonly PermissionStore _permissions;
    private readonly ISessionService _sessions;
    private readonly WorkingIndicator _indicator;
    private readonly int _agentPort;
    private readonly string _frontendOrigin;
    private readonly ILogger<TelegramUpdateService> _log;

    // Failed /link attempts per chat: a guessed code would bind the attacker's chat
    // to a victim's account, so brute-forcing gets cut off after a few tries.
    private sealed record ThrottleBucket(int Count, DateTime WindowStart, bool Warned);
    private readonly ConcurrentDictionary<string, ThrottleBucket> _linkAttempts = new();

    public TelegramUpdateService(TelegramOptions opts, TelegramClient tg, ChatBindingStore bindings,
        ChatLinkCodeStore codes, UserDirectory users, PermissionStore permissions,
        ISessionService sessions, WorkingIndicator indicator, IConfiguration cfg, ILogger<TelegramUpdateService> log)
    {
        _opts = opts; _tg = tg; _bindings = bindings; _codes = codes; _users = users;
        _permissions = permissions; _sessions = sessions; _indicator = indicator;
        _agentPort = cfg.GetValue("AgentHub:AgentPort", 7681);
        _frontendOrigin = (cfg["FrontendOrigin"] ?? "").TrimEnd('/');
        _log = log;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_opts.CanRun)
        {
            _log.LogInformation("Telegram long polling not started (no bot token / disabled).");
            return;
        }

        long offset = 0;
        var lastPrune = DateTime.UtcNow;
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var poll = System.Diagnostics.Stopwatch.StartNew();
                var (next, updates) = await _tg.GetUpdatesAsync(offset, stoppingToken);
                offset = next;

                // A healthy empty poll takes ~50s (the server holds the request). Coming
                // back almost instantly with nothing means getUpdates did NOT long-poll:
                // transport error, immediate ok:false, or Telegram's 409 because a second
                // consumer is polling the same bot — only ONE backend replica may run
                // this service. Back off instead of hammering the API in a tight loop.
                if (updates.Count == 0 && poll.Elapsed < TimeSpan.FromSeconds(5))
                    await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
                foreach (var raw in updates)
                {
                    var u = TelegramUpdate.Parse(raw);
                    if (u is null) continue;
                    try { await DispatchAsync(u, stoppingToken); }
                    catch (OperationCanceledException) { throw; }
                    catch (Exception ex) { _log.LogWarning(ex, "Handling a Telegram update failed; continuing"); }
                }

                if (DateTime.UtcNow - lastPrune > TimeSpan.FromHours(24))
                {
                    await _bindings.PruneMessagesAsync(stoppingToken);
                    foreach (var (chat, bucket) in _linkAttempts)
                        if (DateTime.UtcNow - bucket.WindowStart >= LinkThrottleWindow)
                            _linkAttempts.TryRemove(new KeyValuePair<string, ThrottleBucket>(chat, bucket));
                    lastPrune = DateTime.UtcNow;
                }
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "Telegram poll loop failed; retrying in 5s");
                try { await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken); } catch { break; }
            }
        }
    }

    private Task DispatchAsync(TelegramUpdate u, CancellationToken ct)
        => u.Kind == TelegramUpdateKind.Callback ? HandleCallbackAsync(u, ct) : HandleMessageAsync(u, ct);

    // ------------------------------------------------------------------ callbacks

    /// <summary>Permission button press (perm:&lt;decision&gt;:&lt;id&gt;).</summary>
    private async Task HandleCallbackAsync(TelegramUpdate u, CancellationToken ct)
    {
        if (!PermissionAction.TryParse(u.CallbackData, out var decision, out var reqId))
        {
            await _tg.AnswerCallbackAsync(u.CallbackId!, null, ct);
            return;
        }

        // No sessionId here — a button press only carries the request id.
        var resolved = await _permissions.ResolveAsync(reqId, decision, ct: ct);
        if (resolved is null)
        {
            // Already decided or expired (or unknown) — reflect the final state
            // instead of leaving the press apparently dead.
            var existing = await _permissions.GetAsync(reqId, ct: ct);
            await _tg.AnswerCallbackAsync(u.CallbackId!,
                existing?.Decision == "expired"
                    ? "Expired — answer in the web terminal"
                    : $"Already decided ({existing?.Decision ?? "unknown"})", ct);
            return;
        }

        // Update the prompt message to reflect the decision and drop the buttons.
        if (u.ChatId.Length > 0 && u.MessageId is not null)
        {
            var verb = decision == "deny" ? "⛔ Denied" : "✅ Allowed";
            var suffix = decision == "allowAlways" ? " (won't ask again this run)" : "";
            var by = u.FromUsername is not null ? $" · by @{u.FromUsername}" : "";
            await _tg.EditMessageTextAsync(u.ChatId, u.MessageId, $"{verb} — {resolved.Tool}{suffix}{by}", null, ct);
        }
        await _tg.AnswerCallbackAsync(u.CallbackId!, "Done", ct);
    }

    // ------------------------------------------------------------------- messages

    private async Task HandleMessageAsync(TelegramUpdate u, CancellationToken ct)
    {
        var text = u.Text!.Trim();
        if (text.Length == 0) return;
        var parts = text.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var cmd = parts[0].ToLowerInvariant();
        var arg = parts.Length > 1 ? parts[1] : null;
        // Group chats address commands as "/cmd@BotName" — strip the mention.
        if (cmd.StartsWith('/') && cmd.IndexOf('@') is var at && at > 0) cmd = cmd[..at];

        switch (cmd)
        {
            case "/start" or "/link":
                await HandleLinkAsync(u, arg, ct);
                return;
            case "/sessions" or "!sessions":
                await HandleSessionsAsync(u, ct);
                return;
            case "/use" or "!use":
                await HandleUseAsync(u, arg ?? "", ct);
                return;
            case "/status" or "!status":
                await HandleStatusAsync(u, ct);
                return;
            default:
                await HandlePlainAsync(u, text, ct);
                return;
        }
    }

    /// <summary>Consumes a one-shot link code and binds this chat to the code's owner.</summary>
    private async Task HandleLinkAsync(TelegramUpdate u, string? code, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(code))
        {
            await ReplyAsync(u, "Send /link <code> — get the code from your AgentHub settings.", ct);
            return;
        }

        var now = DateTime.UtcNow;
        // Drop an expired bucket instead of just ignoring it, so the dictionary
        // doesn't accumulate one entry per chat that ever mistyped a code.
        _linkAttempts.TryGetValue(u.ChatId, out var existing);
        if (existing is not null && now - existing.WindowStart >= LinkThrottleWindow)
        {
            _linkAttempts.TryRemove(new KeyValuePair<string, ThrottleBucket>(u.ChatId, existing));
            existing = null;
        }
        var bucket = existing ?? new ThrottleBucket(0, now, false);
        if (bucket.Count >= MaxLinkAttempts)
        {
            if (!bucket.Warned)
            {
                _linkAttempts[u.ChatId] = bucket with { Warned = true };
                await ReplyAsync(u, "Too many attempts — try again later.", ct);
            }
            return;
        }

        var consumed = await _codes.ConsumeAsync(code, "telegram", ct: ct);
        if (consumed is null)
        {
            _linkAttempts[u.ChatId] = bucket with { Count = bucket.Count + 1 };
            await ReplyAsync(u, "⚠️ Invalid or expired code.", ct);
            return;
        }

        _linkAttempts.TryRemove(u.ChatId, out _);
        var stolen = await _users.SetTelegramLinkAsync(consumed.Value.Owner, u.ChatId, u.IsForumChat, ct);
        await ReplyAsync(u, "✅ Linked! Session updates will arrive here."
            + (stolen ? " (This chat was previously linked to another account — that link was replaced.)" : ""), ct);
    }

    /// <summary>Lists this chat's sessions with their live phase.</summary>
    private async Task HandleSessionsAsync(TelegramUpdate u, CancellationToken ct)
    {
        var linked = await _users.GetByTelegramChatAsync(u.ChatId, ct);
        if (linked is null)
        {
            await ReplyAsync(u, "This chat is not linked.", ct);
            return;
        }

        // Bindings left behind by a previous link of this chat (link steal) must not
        // leak another account's session metadata.
        var bindings = (await _bindings.ListByChatAsync("telegram", u.ChatId, ct))
            .Where(b => b.Owner == linked.Owner).ToList();
        if (bindings.Count == 0)
        {
            await ReplyAsync(u, "No sessions in this chat yet.", ct);
            return;
        }

        var lines = new List<string>();
        foreach (var b in bindings)
        {
            var live = await _sessions.GetSessionAsync(b.Owner, b.SessionId, ct);
            lines.Add($"#{ChatFormatting.Tag(b.SessionId)} · {live?.Phase ?? "Unknown"}{(b.Active ? " · active" : "")}");
        }
        await ReplyAsync(u, string.Join("\n", lines), ct);
    }

    /// <summary>Points the chat's plain replies at the session matching the tag.</summary>
    private async Task HandleUseAsync(TelegramUpdate u, string tag, CancellationToken ct)
    {
        var matches = (await _bindings.ListByChatAsync("telegram", u.ChatId, ct))
            .Where(b => ChatFormatting.MatchesTag(tag, b.SessionId)).ToList();
        if (matches.Count == 0)
        {
            await ReplyAsync(u, "No session matches that tag.", ct);
            return;
        }
        if (matches.Count > 1)
        {
            await ReplyAsync(u, "Ambiguous — be more specific.", ct);
            return;
        }

        await _bindings.SetActiveAsync("telegram", u.ChatId, matches[0].SessionId, ct);
        await ReplyAsync(u, $"✅ Plain replies now go to #{ChatFormatting.Tag(matches[0].SessionId)}.", ct);
    }

    /// <summary>Answers with the target session's current state.</summary>
    private async Task HandleStatusAsync(TelegramUpdate u, CancellationToken ct)
    {
        var linked = await _users.GetByTelegramChatAsync(u.ChatId, ct);
        if (linked is null)
        {
            await ReplyAsync(u, "This chat is not linked.", ct);
            return;
        }

        var binding = await ResolveBindingAsync(u, ct);
        // A binding from a previous link of this chat (link steal) must not leak
        // another account's session state — treat it as absent.
        if (binding is null || binding.Owner != linked.Owner)
        {
            await ReplyAsync(u, "No active session in this chat.", ct);
            return;
        }

        var live = await _sessions.GetSessionAsync(binding.Owner, binding.SessionId, ct);
        var pendingTool = await _permissions.GetPendingBySessionAsync(binding.SessionId, ct);
        var link = _frontendOrigin.Length == 0 ? null : $"{_frontendOrigin}/s/{binding.SessionId}";
        await ReplyAsync(u, ChatFormatting.StatusText(
            live?.Phase ?? "Unknown", live?.QuestionPending ?? false, pendingTool, link), ct);
    }

    /// <summary>Plain reply: routed to a session and typed into its terminal.</summary>
    private async Task HandlePlainAsync(TelegramUpdate u, string text, CancellationToken ct)
    {
        var binding = await ResolveBindingAsync(u, ct);
        if (binding is null)
        {
            await ReplyAsync(u, "No active session in this chat. Use /sessions to list.", ct);
            return;
        }

        // Ownership guard: only the account this chat is linked to may drive the
        // session — inherent for DMs, but a group binding must not deliver into a
        // session that stopped belonging to the linked account.
        var linked = await _users.GetByTelegramChatAsync(u.ChatId, ct);
        if (linked is null || linked.Owner != binding.Owner)
        {
            _log.LogWarning("Ignoring Telegram reply for session {Id}: chat is not linked to the session owner", binding.SessionId);
            return;
        }

        var info = await _sessions.GetSessionAsync(binding.Owner, binding.SessionId, ct);
        if (info?.PodIp is not { Length: > 0 } podIp || info.Phase != "Running")
        {
            await ReplyAsync(u, "⚠️ Session is not running — cannot deliver the reply.", ct);
            return;
        }

        await AgentTerminal.SendInputAsync(podIp, _agentPort, text, ct);
        _log.LogInformation("Delivered Telegram reply to session {Id}", binding.SessionId);

        // A previous status message may still be up (second reply while working) — remove it first.
        if (binding.StatusRef is { } oldRef) await _tg.DeleteMessageAsync(binding.ChatId, oldRef, ct);

        // Show a lightweight "working…" indicator until the session's next event.
        var statusId = await _tg.SendMessageAsync(binding.ChatId, WorkingIndicator.Frames[0], binding.ThreadId, null, ct);
        if (statusId is not null)
        {
            await _bindings.SetStatusRefAsync("telegram", binding.SessionId, statusId, ct);
            var chatId = binding.ChatId;
            _indicator.Start(binding.SessionId, (t, c) => _tg.EditMessageTextAsync(chatId, statusId, t, null, c));
        }
    }

    /// <summary>
    /// The session a message is meant for: (1) its forum topic, else (2) the message
    /// it replies to, else (3) the chat's active session.
    /// </summary>
    private async Task<ChatBinding?> ResolveBindingAsync(TelegramUpdate u, CancellationToken ct)
    {
        if (u.ThreadId is not null)
            return await _bindings.GetByThreadAsync("telegram", u.ChatId, u.ThreadId, ct);
        if (u.ReplyToMessageId is not null)
        {
            var sessionId = await _bindings.GetSessionByMessageAsync("telegram", u.ChatId, u.ReplyToMessageId, ct);
            return sessionId is null ? null : await _bindings.GetAsync("telegram", sessionId, ct);
        }
        return await _bindings.GetActiveAsync("telegram", u.ChatId, ct);
    }

    private Task ReplyAsync(TelegramUpdate u, string text, CancellationToken ct)
        => _tg.SendMessageAsync(u.ChatId, text, u.ThreadId, null, ct);
}
