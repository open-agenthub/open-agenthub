using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace AgentHub.Api.Services;

/// <summary>
/// One turn of a conversation, read from the provider's own transcript file.
/// </summary>
/// <param name="Role"><c>user</c>, <c>assistant</c>, <c>tool</c> (a call, with <paramref name="Tool"/>
/// naming it) or <c>result</c> (what the call returned).</param>
/// <param name="Text">The text of the turn; tool arguments and results are cut to
/// <see cref="NativeTranscript.MaxToolChars"/>.</param>
/// <param name="At">The provider's timestamp for the line, when it recorded one.</param>
/// <param name="Tool">The tool a <c>tool</c> entry called.</param>
public sealed record TranscriptEntry(string Role, string Text, DateTime? At, string? Tool = null);

/// <summary>
/// Reads the JSONL files the agent CLIs write for themselves — Claude Code's project transcript,
/// Codex's rollout, and the stream-json a chat session's pipe carries — into role-tagged entries.
///
/// The formats are different but both are "one JSON object per line, a type tag, a message with
/// a role and content parts", so one reader with a per-line format switch is simpler than two
/// readers plus a dispatcher. A line that does not parse, or is a record type the reader does
/// not know (file snapshots, token counts, reasoning), is skipped: a transcript that trails off
/// mid-write must still render what it has.
/// </summary>
public static partial class NativeTranscript
{
    public const int MaxToolChars = 2_000;

    // Codex wraps the instructions and environment it injects as user messages in a single XML
    // element; a person never typed those, and rendering them as "User" would make every
    // transcript open with the contents of AGENTS.md.
    [GeneratedRegex(@"^<([a-z_]+)>[\s\S]*</\1>\s*$")]
    private static partial Regex WrappedInjection();

    public static IReadOnlyList<TranscriptEntry> Parse(string? jsonl)
    {
        var entries = new List<TranscriptEntry>();
        if (string.IsNullOrEmpty(jsonl)) return entries;
        foreach (var raw in jsonl.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            if (line.Length == 0 || line[0] != '{') continue;
            try
            {
                using var document = JsonDocument.Parse(line);
                var root = document.RootElement;
                if (root.ValueKind != JsonValueKind.Object) continue;
                if (root.TryGetProperty("payload", out var payload) && payload.ValueKind == JsonValueKind.Object)
                    ParseCodexLine(root, payload, entries);
                else
                    ParseClaudeLine(root, entries);
            }
            catch (JsonException)
            {
                // A truncated last line while the agent is still writing, or a cap cut that
                // landed inside a record: nothing to recover from it.
            }
        }
        return entries;
    }

    /// <summary>
    /// The transcript as plain text, for callers that read rather than render: the remote API,
    /// the MCP tools and the chat relays. Append-only in the same way the entries are, so a
    /// character offset into it stays valid as the session goes on.
    /// </summary>
    public static string Render(IReadOnlyList<TranscriptEntry> entries)
    {
        var sb = new StringBuilder();
        foreach (var entry in entries)
        {
            if (sb.Length > 0) sb.Append('\n');
            sb.Append("## ").Append(entry.Role switch
            {
                "user" => "User",
                "assistant" => "Assistant",
                "tool" => "Tool: " + (entry.Tool ?? "?"),
                "result" => "Result",
                _ => entry.Role
            }).Append('\n').Append(entry.Text.TrimEnd()).Append('\n');
        }
        return sb.ToString();
    }

    /// <summary>
    /// The last <paramref name="maxChars"/> characters, cut forward to a line boundary so the
    /// first line kept is a whole record. Same rule the session agent applies before uploading.
    /// </summary>
    public static string TrimToLineCap(string jsonl, int maxChars)
    {
        if (jsonl.Length <= maxChars) return jsonl;
        var cut = jsonl[^maxChars..];
        var newline = cut.IndexOf('\n');
        return newline < 0 ? cut : cut[(newline + 1)..];
    }

    // ------------------------------------------------------------------ Claude Code

    private static void ParseClaudeLine(JsonElement root, List<TranscriptEntry> entries)
    {
        var type = root.TryGetProperty("type", out var t) && t.ValueKind == JsonValueKind.String ? t.GetString() : null;
        if (type is not ("user" or "assistant")) return;
        // Meta lines are the CLI talking to itself (command caveats, injected tool guidance);
        // sidechains are sub-agents whose turns the main conversation already summarises.
        if (IsTrue(root, "isMeta") || IsTrue(root, "isSidechain")) return;
        if (!root.TryGetProperty("message", out var message) || message.ValueKind != JsonValueKind.Object) return;
        var at = Timestamp(root, "timestamp");

        if (!message.TryGetProperty("content", out var content)) return;
        if (content.ValueKind == JsonValueKind.String)
        {
            AddText(entries, type, content.GetString(), at);
            return;
        }
        if (content.ValueKind != JsonValueKind.Array) return;

        var text = new StringBuilder();
        foreach (var part in content.EnumerateArray())
        {
            var partType = part.TryGetProperty("type", out var pt) ? pt.GetString() : null;
            switch (partType)
            {
                case "text":
                    if (part.TryGetProperty("text", out var textValue) && textValue.ValueKind == JsonValueKind.String)
                    {
                        if (text.Length > 0) text.Append('\n');
                        text.Append(textValue.GetString());
                    }
                    break;
                case "tool_use":
                    Flush(entries, type, text, at);
                    entries.Add(new TranscriptEntry("tool",
                        Clip(part.TryGetProperty("input", out var input) ? input.GetRawText() : ""), at,
                        part.TryGetProperty("name", out var name) ? name.GetString() : null));
                    break;
                case "tool_result":
                    Flush(entries, type, text, at);
                    entries.Add(new TranscriptEntry("result", Clip(ContentText(part, "content")), at));
                    break;
            }
        }
        Flush(entries, type, text, at);
    }

    // ------------------------------------------------------------------ Codex

    private static void ParseCodexLine(JsonElement root, JsonElement payload, List<TranscriptEntry> entries)
    {
        var lineType = root.TryGetProperty("type", out var lt) ? lt.GetString() : null;
        // event_msg lines repeat what response_item lines already carry; keeping both would
        // show every message twice.
        if (lineType != "response_item") return;
        var at = Timestamp(root, "timestamp");
        var itemType = payload.TryGetProperty("type", out var it) ? it.GetString() : null;
        switch (itemType)
        {
            case "message":
            {
                var role = payload.TryGetProperty("role", out var r) ? r.GetString() : null;
                if (role is not ("user" or "assistant")) return;
                var text = ContentText(payload, "content");
                if (role == "user" && WrappedInjection().IsMatch(text.Trim())) return;
                AddText(entries, role, text, at);
                return;
            }
            case "function_call":
            case "custom_tool_call":
                entries.Add(new TranscriptEntry("tool",
                    Clip(StringOr(payload, "arguments") ?? StringOr(payload, "input") ?? ""), at,
                    StringOr(payload, "name")));
                return;
            case "local_shell_call":
                entries.Add(new TranscriptEntry("tool", Clip(ShellCommand(payload)), at, "shell"));
                return;
            case "function_call_output":
            case "custom_tool_call_output":
                entries.Add(new TranscriptEntry("result", Clip(ContentText(payload, "output")), at));
                return;
        }
    }

    private static string ShellCommand(JsonElement payload)
    {
        if (!payload.TryGetProperty("action", out var action) || action.ValueKind != JsonValueKind.Object) return "";
        if (action.TryGetProperty("command", out var command) && command.ValueKind == JsonValueKind.Array)
            return string.Join(' ', command.EnumerateArray().Select(c => c.GetString() ?? ""));
        return action.GetRawText();
    }

    // ------------------------------------------------------------------ shared

    private static void AddText(List<TranscriptEntry> entries, string role, string? text, DateTime? at)
    {
        if (!string.IsNullOrWhiteSpace(text)) entries.Add(new TranscriptEntry(role, text, at));
    }

    private static void Flush(List<TranscriptEntry> entries, string role, StringBuilder text, DateTime? at)
    {
        if (text.Length == 0) return;
        AddText(entries, role, text.ToString(), at);
        text.Clear();
    }

    /// <summary>A content part that is either a string or an array of typed text parts.</summary>
    private static string ContentText(JsonElement element, string property)
    {
        if (!element.TryGetProperty(property, out var value)) return "";
        switch (value.ValueKind)
        {
            case JsonValueKind.String:
                return value.GetString() ?? "";
            case JsonValueKind.Array:
                return string.Join('\n', value.EnumerateArray()
                    .Select(part => part.ValueKind == JsonValueKind.String
                        ? part.GetString()
                        : part.TryGetProperty("text", out var t) && t.ValueKind == JsonValueKind.String ? t.GetString() : null)
                    .Where(s => !string.IsNullOrEmpty(s)));
            case JsonValueKind.Object:
                // Codex wraps a tool's output as {content, success}.
                return ContentText(value, "content");
            default:
                return "";
        }
    }

    private static string? StringOr(JsonElement element, string property)
        => element.TryGetProperty(property, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static bool IsTrue(JsonElement element, string property)
        => element.TryGetProperty(property, out var v) && v.ValueKind == JsonValueKind.True;

    private static DateTime? Timestamp(JsonElement element, string property)
        => element.TryGetProperty(property, out var v) && v.ValueKind == JsonValueKind.String
           && DateTime.TryParse(v.GetString(), null, System.Globalization.DateTimeStyles.AdjustToUniversal
               | System.Globalization.DateTimeStyles.AssumeUniversal, out var at)
            ? at : null;

    private static string Clip(string text)
        => text.Length <= MaxToolChars ? text : text[..MaxToolChars] + "…";
}
