using System.Globalization;
using System.Text.Json;

namespace AgentHub.Api.Chat.Signal;

/// <summary>One parsed inbound Signal event we care about: a text message (optionally quoting one of
/// our messages) or a reaction. Timestamps are Signal's message identifiers — kept as strings.</summary>
public sealed record SignalEnvelope(
    string Sender, string Timestamp, string? Text,
    string? QuotedTimestamp, string? ReactionEmoji, string? ReactionTargetTimestamp)
{
    /// <summary>Parses one signal-cli-rest-api receive frame. Returns null for everything we ignore:
    /// receipts, typing/sync messages, reaction removals, empty data messages and invalid JSON.</summary>
    public static SignalEnvelope? Parse(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("envelope", out var env)) return null;

            var sender = env.TryGetProperty("sourceNumber", out var sn) && sn.ValueKind == JsonValueKind.String
                ? sn.GetString()
                : env.TryGetProperty("source", out var s) && s.ValueKind == JsonValueKind.String ? s.GetString() : null;
            if (string.IsNullOrEmpty(sender)) return null;

            if (!env.TryGetProperty("timestamp", out var ts) || !ts.TryGetInt64(out var timestamp)) return null;
            if (!env.TryGetProperty("dataMessage", out var data)) return null;

            var tsStr = timestamp.ToString(CultureInfo.InvariantCulture);

            if (data.TryGetProperty("reaction", out var reaction))
            {
                if (reaction.TryGetProperty("isRemove", out var rem) && rem.ValueKind == JsonValueKind.True)
                    return null;
                if (reaction.TryGetProperty("emoji", out var emoji) && emoji.GetString() is { Length: > 0 } em &&
                    reaction.TryGetProperty("targetSentTimestamp", out var target) && target.TryGetInt64(out var targetTs))
                {
                    return new SignalEnvelope(sender, tsStr, Text: null, QuotedTimestamp: null,
                        ReactionEmoji: em,
                        ReactionTargetTimestamp: targetTs.ToString(CultureInfo.InvariantCulture));
                }
                return null;
            }

            if (data.TryGetProperty("message", out var msg) && msg.GetString() is { Length: > 0 } text)
            {
                string? quoted = null;
                if (data.TryGetProperty("quote", out var quote) &&
                    quote.TryGetProperty("id", out var qid) && qid.TryGetInt64(out var quotedTs))
                    quoted = quotedTs.ToString(CultureInfo.InvariantCulture);
                return new SignalEnvelope(sender, tsStr, text, quoted,
                    ReactionEmoji: null, ReactionTargetTimestamp: null);
            }

            return null;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
