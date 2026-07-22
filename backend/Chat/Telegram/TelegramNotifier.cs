using AgentHub.Api.Notifications;
using AgentHub.Api.Persistence;

namespace AgentHub.Api.Chat.Telegram;

/// <summary>
/// Notifier that mirrors a session into the owner's Telegram chat: on every
/// "question" event it sends the agent's question (split across messages when
/// long), on finished/failed a closing line. Replies are handled by the
/// Telegram poll service. Community feature — no license required.
/// </summary>
public sealed class TelegramNotifier : INotifier
{
    private readonly TelegramOptions _opts;
    private readonly TelegramClient _tg;
    private readonly ChatBindingStore _bindings;
    private readonly UserDirectory _users;
    private readonly WorkingIndicator _indicator;
    private readonly string _frontendOrigin;
    private readonly ILogger<TelegramNotifier> _log;

    public TelegramNotifier(TelegramOptions opts, TelegramClient tg, ChatBindingStore bindings,
        UserDirectory users, WorkingIndicator indicator, IConfiguration cfg, ILogger<TelegramNotifier> log)
    {
        _opts = opts; _tg = tg; _bindings = bindings; _users = users; _indicator = indicator;
        _frontendOrigin = (cfg["FrontendOrigin"] ?? "").TrimEnd('/');
        _log = log;
    }

    public async Task NotifyAsync(SessionRecord s, string eventType, string message, CancellationToken ct = default)
    {
        if (!_opts.CanRun) return;
        try
        {
            var binding = await _bindings.GetAsync("telegram", s.Id, ct);

            // The session produced an event — the working indicator (if any) is obsolete.
            _indicator.Stop(s.Id);

            // Chats get re-linked and users opt out: the stored binding is only a hint —
            // the user row decides where (and whether) to send.
            if (binding is not null && !await IsBindingCurrentAsync(binding, ct))
            {
                if (eventType != "question") return; // never send to a stale/opted-out target
                binding = null; // the create path re-validates and re-binds at the current chat
            }

            if (binding?.StatusRef is { } statusRef)
            {
                await _tg.DeleteMessageAsync(binding.ChatId, statusRef, ct);
                await _bindings.SetStatusRefAsync("telegram", s.Id, null, ct);
            }

            if (eventType is "finished" or "failed")
            {
                if (binding is not null)
                    await _tg.SendMessageAsync(binding.ChatId, $"🏁 {eventType} — {message}", binding.ThreadId, null, ct);
                return;
            }
            if (eventType != "question") return;

            if (binding is null)
            {
                binding = await CreateBindingAsync(s, ct);
                if (binding is null) return; // user not linked / opted out
            }

            // The most recent question owns the chat's plain replies (DM reply-routing mode).
            await _bindings.SetActiveAsync("telegram", binding.ChatId, s.Id, ct);

            var messages = BuildAnswerMessages(message);
            for (var i = 0; i < messages.Count; i++)
            {
                if (i > 0) await Task.Delay(1100, ct); // Telegram tolerates ~1 msg/s per chat
                var mid = await _tg.SendMessageAsync(binding.ChatId, messages[i], binding.ThreadId, null, ct);
                if (mid is null)
                {
                    _log.LogWarning("Telegram chunk {Index}/{Count} failed for session {Id} — stopping to avoid silent gaps", i + 1, messages.Count, s.Id);
                    break;
                }
                await _bindings.RecordMessageAsync("telegram", binding.ChatId, mid, s.Id, ct);
            }
        }
        catch (Exception ex) { _log.LogWarning(ex, "Telegram notify failed for session {Id}", s.Id); }
    }

    /// <summary>
    /// First contact: creates the session's conversation in the owner's linked chat —
    /// a forum topic when the chat is a forum group (falling back to the main chat when
    /// topic creation fails), plus a header message with the session link and reply
    /// instructions. Null when the owner is not linked or opted out, or the header
    /// could not be sent.
    /// </summary>
    private async Task<ChatBinding?> CreateBindingAsync(SessionRecord s, CancellationToken ct)
    {
        var user = await _users.GetAsync(s.Owner, ct);
        if (user is not { TelegramEnabled: true, TelegramChatId: not null }) return null;

        string? threadId = null;
        if (user.TelegramForum)
        {
            threadId = await _tg.CreateForumTopicAsync(user.TelegramChatId, $"{s.Title} #{ChatFormatting.Tag(s.Id)}", ct);
            if (threadId is null)
                _log.LogWarning("Telegram forum topic creation failed for session {Id} — using the main chat", s.Id);
        }

        var header = ChatFormatting.Header(s.Id, s.Title) + $" ({s.Mode})\n" +
                     (string.IsNullOrEmpty(_frontendOrigin) ? "" : $"{_frontendOrigin}/s/{s.Id}\n") +
                     (threadId is not null
                         ? "Reply in this topic to answer. !status shows progress."
                         : $"Reply to a message of this session (or /use {ChatFormatting.Tag(s.Id)}) to answer. !status shows progress.") +
                     // Group chats carry chat-level authority (like Slack threads): make that visible.
                     (user.TelegramForum ? "\nNote: everyone in this group can reply and approve permissions." : "");

        var headerId = await _tg.SendMessageAsync(user.TelegramChatId, header, threadId, null, ct);
        if (headerId is null) return null;

        // Active=false per the store contract — SetActiveAsync flips it right after.
        var binding = new ChatBinding("telegram", s.Id, s.Owner, user.TelegramChatId, threadId, null, false);
        await _bindings.UpsertAsync(binding, ct);
        await _bindings.RecordMessageAsync("telegram", binding.ChatId, headerId, s.Id, ct);
        return binding;
    }

    /// <summary>True when the binding's target still is the owner's currently linked,
    /// enabled Telegram chat — a stale target must never receive session content.</summary>
    private async Task<bool> IsBindingCurrentAsync(ChatBinding binding, CancellationToken ct)
    {
        var user = await _users.GetAsync(binding.Owner, ct);
        return user is { TelegramEnabled: true } && user.TelegramChatId == binding.ChatId;
    }

    /// <summary>
    /// Builds the labeled Telegram messages for one agent answer (pure — exposed for
    /// tests). Telegram messages go out without parse_mode, so the text stays verbatim.
    /// Thin wrapper over the shared <see cref="ChatFormatting.BuildAnswerMessages"/>.
    /// </summary>
    public static IReadOnlyList<string> BuildAnswerMessages(string message)
        => ChatFormatting.BuildAnswerMessages(message);
}
