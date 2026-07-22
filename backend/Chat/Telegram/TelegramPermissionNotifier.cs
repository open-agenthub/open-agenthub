using AgentHub.Api.Permissions;
using AgentHub.Api.Persistence;

namespace AgentHub.Api.Chat.Telegram;

/// <summary>
/// Posts a tool-permission request to the session's Telegram conversation (or the
/// owner's linked DM) as a message with Allow / Always / Deny inline buttons. The
/// button press is handled by <see cref="TelegramUpdateService"/>. Returns false
/// when the user has no Telegram target (or the integration is off), so the caller
/// falls back to the next notifier / the normal prompt. Community feature — no
/// license required.
/// </summary>
public sealed class TelegramPermissionNotifier : IPermissionNotifier, IPermissionPromptEditor
{
    public string Platform => "telegram";

    private readonly TelegramOptions _opts;
    private readonly TelegramClient _tg;
    private readonly ChatBindingStore _bindings;
    private readonly UserDirectory _users;
    private readonly PermissionStore _store;

    public TelegramPermissionNotifier(TelegramOptions opts, TelegramClient tg,
        ChatBindingStore bindings, UserDirectory users, PermissionStore store)
    { _opts = opts; _tg = tg; _bindings = bindings; _users = users; _store = store; }

    public async Task<bool> PostAsync(PermissionRequest req, CancellationToken ct = default)
    {
        if (!_opts.CanRun) return false;

        // Prefer the session's existing conversation (topic/DM), else the owner's DM.
        // A binding whose target no longer matches the owner's currently linked, enabled
        // chat (chats get re-linked, users opt out) is treated as absent.
        string? chatId, threadId;
        var binding = await _bindings.GetAsync("telegram", req.SessionId, ct);
        if (binding is not null && await IsBindingCurrentAsync(binding, ct))
        {
            chatId = binding.ChatId;
            threadId = binding.ThreadId;
        }
        else
        {
            var user = await _users.GetAsync(req.Owner, ct);
            if (user is not { TelegramEnabled: true, TelegramChatId: not null }) return false;
            chatId = user.TelegramChatId;
            threadId = null;
        }

        // Plain text (no parse_mode) — the summary goes out verbatim, just capped.
        var text = $"🔒 The agent wants to use {req.Tool}.";
        if (!string.IsNullOrWhiteSpace(req.Summary)) text += "\n" + Trim(req.Summary!, 600);

        var markup = new { inline_keyboard = new[] { new object[] {
            new { text = "✅ Allow", callback_data = PermissionAction.Id("allow", req.Id) },
            new { text = "✅ Always", callback_data = PermissionAction.Id("allowAlways", req.Id) },
            new { text = "⛔ Deny",  callback_data = PermissionAction.Id("deny", req.Id) } } } };

        var mid = await _tg.SendMessageAsync(chatId, text, threadId, markup, ct);
        if (mid is null) return false;
        await _store.SetPromptMessageAsync(req.Id, "telegram", chatId, mid, ct);
        return true;
    }

    /// <summary>True when the binding's target still is the owner's currently linked,
    /// enabled Telegram chat — a stale target must never receive a permission prompt.</summary>
    private async Task<bool> IsBindingCurrentAsync(ChatBinding binding, CancellationToken ct)
    {
        var user = await _users.GetAsync(binding.Owner, ct);
        return user is { TelegramEnabled: true } && user.TelegramChatId == binding.ChatId;
    }

    /// <summary>The request can no longer be answered: drop the buttons so the prompt doesn't look alive.</summary>
    public async Task MarkExpiredAsync(PermissionRequest req, CancellationToken ct = default)
    {
        if (req.Channel is null || req.MessageTs is null) return;
        // markup null removes the inline keyboard.
        await _tg.EditMessageTextAsync(req.Channel, req.MessageTs, ExpiredText(req.Tool), null, ct);
    }

    /// <summary>Prompt text once the request can no longer be answered out-of-band.</summary>
    public static string ExpiredText(string tool)
        => $"⏰ Expired — {tool}. Please answer in the web terminal.";

    private static string Trim(string s, int max) => s.Length <= max ? s : s[..max] + " …";
}
