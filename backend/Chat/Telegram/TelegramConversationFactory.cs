using AgentHub.Api.Models;
using AgentHub.Api.Persistence;

namespace AgentHub.Api.Chat.Telegram;

/// <summary>
/// Creates a session's conversation in the owner's linked Telegram chat: a forum topic
/// when the chat is a forum group (falling back to the main chat when topic creation
/// fails), plus a header message with the session link and reply instructions. Shared
/// by <see cref="TelegramNotifier"/> (first question event) and
/// <see cref="TelegramUpdateService"/> (/new command).
/// </summary>
public sealed class TelegramConversationFactory
{
    private readonly TelegramClient _tg;
    private readonly ChatBindingStore _bindings;
    private readonly UserDirectory _users;
    private readonly string _frontendOrigin;
    private readonly ILogger<TelegramConversationFactory> _log;

    public TelegramConversationFactory(TelegramClient tg, ChatBindingStore bindings, UserDirectory users,
        IConfiguration cfg, ILogger<TelegramConversationFactory> log)
    {
        _tg = tg; _bindings = bindings; _users = users;
        _frontendOrigin = (cfg["FrontendOrigin"] ?? "").TrimEnd('/');
        _log = log;
    }

    /// <summary>
    /// Null when the owner is not linked or opted out, or the header could not be sent.
    /// The binding is stored with Active=false (store contract) — callers flip it via
    /// <see cref="ChatBindingStore.SetActiveAsync"/> when appropriate.
    /// </summary>
    public async Task<ChatBinding?> CreateBindingAsync(string sessionId, string title, string owner,
        SessionMode mode, CancellationToken ct)
    {
        var user = await _users.GetAsync(owner, ct);
        if (user is not { TelegramEnabled: true, TelegramChatId: not null }) return null;

        string? threadId = null;
        if (user.TelegramForum)
        {
            threadId = await _tg.CreateForumTopicAsync(user.TelegramChatId, $"{title} #{ChatFormatting.Tag(sessionId)}", ct);
            if (threadId is null)
                _log.LogWarning("Telegram forum topic creation failed for session {Id} — using the main chat", sessionId);
        }

        var header = ChatFormatting.Header(sessionId, title) + $" ({mode})\n" +
                     (string.IsNullOrEmpty(_frontendOrigin) ? "" : $"{_frontendOrigin}/s/{sessionId}\n") +
                     (threadId is not null
                         ? "Reply in this topic to answer. !status shows progress."
                         : $"Reply to a message of this session (or /use {ChatFormatting.Tag(sessionId)}) to answer. !status shows progress.") +
                     // Group chats carry chat-level authority (like Slack threads): make that visible.
                     (user.TelegramForum ? "\nNote: everyone in this group can reply and approve permissions." : "");

        var headerId = await _tg.SendMessageAsync(user.TelegramChatId, header, threadId, null, ct);
        if (headerId is null) return null;

        var binding = new ChatBinding("telegram", sessionId, owner, user.TelegramChatId, threadId, null, false);
        await _bindings.UpsertAsync(binding, ct);
        await _bindings.RecordMessageAsync("telegram", binding.ChatId, headerId, sessionId, ct);
        return binding;
    }
}
