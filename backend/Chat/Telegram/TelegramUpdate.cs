using System.Globalization;
using System.Text.Json;

namespace AgentHub.Api.Chat.Telegram;

public enum TelegramUpdateKind { Message, Callback }

/// <summary>One parsed Telegram update relevant to us (plain message or permission button callback).
/// Ids are kept as strings — the Bot API uses 64-bit ints that we only pass back verbatim.</summary>
public sealed record TelegramUpdate(
    TelegramUpdateKind Kind, long UpdateId, string ChatId,
    string? ThreadId, string? MessageId, string? Text, string? ReplyToMessageId,
    bool IsForumChat, string? FromUsername, string? CallbackData, string? CallbackId)
{
    /// <summary>Parses one element of getUpdates' result[]. Returns null for updates we ignore
    /// (bot echoes, edited messages, messages without text, service types, unparseable JSON).</summary>
    public static TelegramUpdate? Parse(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            var updateId = root.TryGetProperty("update_id", out var uid) ? uid.GetInt64() : 0L;

            if (root.TryGetProperty("callback_query", out var cb))
                return ParseCallback(cb, updateId);
            if (root.TryGetProperty("message", out var msg))
                return ParseMessage(msg, updateId);
            return null; // edited_message, channel_post, etc.
        }
        catch (JsonException) { return null; }
    }

    private static TelegramUpdate? ParseCallback(JsonElement cb, long updateId)
    {
        if (!cb.TryGetProperty("id", out var id) || id.GetString() is not { Length: > 0 } callbackId) return null;
        if (!cb.TryGetProperty("data", out var data) || data.GetString() is not { Length: > 0 } callbackData) return null;

        string? chatId = null, messageId = null;
        if (cb.TryGetProperty("message", out var msg))
        {
            if (msg.TryGetProperty("chat", out var chat) && chat.TryGetProperty("id", out var cid))
                chatId = AsIdString(cid);
            if (msg.TryGetProperty("message_id", out var mid))
                messageId = AsIdString(mid);
        }

        return new TelegramUpdate(
            TelegramUpdateKind.Callback, updateId, chatId ?? "",
            ThreadId: null, MessageId: messageId, Text: null, ReplyToMessageId: null,
            IsForumChat: false, FromUsername: Username(cb),
            CallbackData: callbackData, CallbackId: callbackId);
    }

    private static TelegramUpdate? ParseMessage(JsonElement msg, long updateId)
    {
        if (!msg.TryGetProperty("chat", out var chat) || !chat.TryGetProperty("id", out var cid)) return null;
        if (msg.TryGetProperty("from", out var from) &&
            from.TryGetProperty("is_bot", out var isBot) && isBot.GetBoolean()) return null;
        if (!msg.TryGetProperty("text", out var textEl) || textEl.GetString() is not { Length: > 0 } text) return null;

        var messageId = msg.TryGetProperty("message_id", out var mid) ? AsIdString(mid) : null;
        var threadId = msg.TryGetProperty("message_thread_id", out var tid) ? AsIdString(tid) : null;

        string? replyTo = null;
        if (msg.TryGetProperty("reply_to_message", out var reply) &&
            reply.TryGetProperty("message_id", out var rid))
        {
            replyTo = AsIdString(rid);
            // Forum topics: every message "replies" to the topic-creation service message —
            // that pseudo-reply carries no meaning, so drop it and keep only the thread id.
            if (replyTo == threadId) replyTo = null;
        }

        var isForum = chat.TryGetProperty("is_forum", out var forum) && forum.ValueKind == JsonValueKind.True;

        return new TelegramUpdate(
            TelegramUpdateKind.Message, updateId, AsIdString(cid),
            ThreadId: threadId, MessageId: messageId, Text: text, ReplyToMessageId: replyTo,
            IsForumChat: isForum, FromUsername: Username(msg),
            CallbackData: null, CallbackId: null);
    }

    private static string? Username(JsonElement parent)
        => parent.TryGetProperty("from", out var from) && from.TryGetProperty("username", out var un)
            ? un.GetString() : null;

    private static string AsIdString(JsonElement number)
        => number.GetInt64().ToString(CultureInfo.InvariantCulture);
}
