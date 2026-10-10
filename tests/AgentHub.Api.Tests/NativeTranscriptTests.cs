using AgentHub.Api.Services;
using Xunit;

namespace AgentHub.Api.Tests;

/// <summary>
/// Reading the agent CLIs' own transcript files. The fixtures are the line shapes the pinned
/// CLIs write (checked against a real Claude Code project file and the Codex rollout format),
/// trimmed to the fields the reader looks at.
/// </summary>
public class NativeTranscriptTests
{
    private const string ClaudeJsonl =
        """
        {"type":"file-history-snapshot","messageId":"m0","snapshot":{}}
        {"type":"user","message":{"role":"user","content":"Fix the failing build"},"uuid":"u1","timestamp":"2026-10-10T07:30:08.959Z","cwd":"/workspace/repo","sessionId":"s"}
        {"type":"user","message":{"role":"user","content":"<local-command-caveat>Caveat: ...</local-command-caveat>"},"isMeta":true,"uuid":"u2","timestamp":"2026-10-10T07:30:09.000Z"}
        {"type":"assistant","message":{"role":"assistant","content":[{"type":"thinking","thinking":"private"}]},"uuid":"a1","timestamp":"2026-10-10T07:30:12.712Z"}
        {"type":"assistant","message":{"role":"assistant","content":[{"type":"text","text":"Looking at the tests."},{"type":"tool_use","id":"t1","name":"Bash","input":{"command":"npm test"}}]},"uuid":"a2","timestamp":"2026-10-10T07:30:13.051Z"}
        {"type":"user","message":{"role":"user","content":[{"tool_use_id":"t1","type":"tool_result","content":"3 passing"}]},"uuid":"u3","timestamp":"2026-10-10T07:30:14.527Z"}
        {"type":"assistant","message":{"role":"assistant","content":[{"type":"text","text":"Sub-agent chatter"}]},"isSidechain":true,"uuid":"a3"}
        {"type":"system","subtype":"turn_duration","durationMs":29793}
        {"type":"assistant","message":{"role":"assistant","content":[{"type":"text","text":"All green."}]},"uuid":"a4","timestamp":"2026-10-10T07:30:38.000Z"}
        {"type":"assistant","message":{"role":"assistant","content":[{"type":"text","text":"cut off mid-wr
        """;

    private const string CodexJsonl =
        """
        {"timestamp":"2026-10-10T10:00:00.000Z","type":"session_meta","payload":{"id":"11111111-1111-4111-8111-111111111111","cwd":"/workspace/repo","cli_version":"0.160.0"}}
        {"timestamp":"2026-10-10T10:00:00.100Z","type":"response_item","payload":{"type":"message","role":"user","content":[{"type":"input_text","text":"<user_instructions>\nBe terse.\n</user_instructions>"}]}}
        {"timestamp":"2026-10-10T10:00:00.200Z","type":"response_item","payload":{"type":"message","role":"user","content":[{"type":"input_text","text":"<environment_context>\n<cwd>/workspace/repo</cwd>\n</environment_context>"}]}}
        {"timestamp":"2026-10-10T10:00:01.000Z","type":"response_item","payload":{"type":"message","role":"user","content":[{"type":"input_text","text":"Triage the build"}]}}
        {"timestamp":"2026-10-10T10:00:01.000Z","type":"event_msg","payload":{"type":"user_message","message":"Triage the build"}}
        {"timestamp":"2026-10-10T10:00:02.000Z","type":"response_item","payload":{"type":"reasoning","summary":[]}}
        {"timestamp":"2026-10-10T10:00:03.000Z","type":"response_item","payload":{"type":"function_call","name":"shell","arguments":"{\"command\":[\"npm\",\"test\"]}","call_id":"c1"}}
        {"timestamp":"2026-10-10T10:00:04.000Z","type":"response_item","payload":{"type":"function_call_output","call_id":"c1","output":{"content":"3 passing","success":true}}}
        {"timestamp":"2026-10-10T10:00:05.000Z","type":"response_item","payload":{"type":"message","role":"assistant","content":[{"type":"output_text","text":"The build is green."}]}}
        {"timestamp":"2026-10-10T10:00:05.000Z","type":"event_msg","payload":{"type":"agent_message","message":"The build is green."}}
        """;

    [Fact]
    public void Claude_ReadsPeopleTurnsToolCallsAndResultsAndSkipsTheRest()
    {
        var entries = NativeTranscript.Parse(ClaudeJsonl);

        Assert.Equal(
        [
            ("user", "Fix the failing build", (string?)null),
            ("assistant", "Looking at the tests.", null),
            ("tool", """{"command":"npm test"}""", "Bash"),
            ("result", "3 passing", null),
            ("assistant", "All green.", null)
        ], entries.Select(e => (e.Role, e.Text, e.Tool)));
        Assert.Equal(new DateTime(2026, 10, 10, 7, 30, 8, 959, DateTimeKind.Utc), entries[0].At);
    }

    [Fact]
    public void Codex_ReadsResponseItemsOnceAndDropsInjectedInstructions()
    {
        var entries = NativeTranscript.Parse(CodexJsonl);

        Assert.Equal(
        [
            ("user", "Triage the build", (string?)null),
            ("tool", """{"command":["npm","test"]}""", "shell"),
            ("result", "3 passing", null),
            ("assistant", "The build is green.", null)
        ], entries.Select(e => (e.Role, e.Text, e.Tool)));
    }

    [Fact]
    public void ChatStreamJson_IsTheSameShapeAndReadsTheEchoedUserTurn()
    {
        // A chat session's scrollback is the stream-json the pipe carried, with the session
        // agent's own echo of what the person typed (the CLI never echoes it).
        const string stream =
            """
            {"type":"system","subtype":"init","model":"m"}
            {"type":"user","message":{"role":"user","content":[{"type":"text","text":"hello"}]},"agenthub_echo":true}
            {"type":"assistant","message":{"role":"assistant","content":[{"type":"text","text":"hi"}]}}
            {"type":"result","is_error":false}
            """;

        var entries = NativeTranscript.Parse(stream);

        Assert.Equal([("user", "hello"), ("assistant", "hi")], entries.Select(e => (e.Role, e.Text)));
    }

    [Fact]
    public void ToolArgumentsAndResultsAreClipped()
    {
        var huge = new string('x', NativeTranscript.MaxToolChars + 50);
        var jsonl = "{\"type\":\"user\",\"message\":{\"role\":\"user\",\"content\":[{\"type\":\"tool_result\",\"content\":\"" + huge + "\"}]}}";

        var entry = Assert.Single(NativeTranscript.Parse(jsonl));

        Assert.Equal(NativeTranscript.MaxToolChars + 1, entry.Text.Length);
        Assert.EndsWith("…", entry.Text);
    }

    [Fact]
    public void Render_IsReadableTextWithRoleHeadings()
    {
        var text = NativeTranscript.Render(NativeTranscript.Parse(ClaudeJsonl));

        Assert.Equal(
            "## User\nFix the failing build\n\n" +
            "## Assistant\nLooking at the tests.\n\n" +
            "## Tool: Bash\n{\"command\":\"npm test\"}\n\n" +
            "## Result\n3 passing\n\n" +
            "## Assistant\nAll green.\n", text);
    }

    [Fact]
    public void Render_IsAppendOnlyAsTheTranscriptGrows()
    {
        // The remote API and the MCP tools page the rendered text by character offset; a
        // longer transcript must start with the shorter one's rendering or every cursor breaks.
        var lines = ClaudeJsonl.Split('\n');
        var shorter = NativeTranscript.Render(NativeTranscript.Parse(string.Join('\n', lines.Take(6))));
        var longer = NativeTranscript.Render(NativeTranscript.Parse(ClaudeJsonl));

        Assert.StartsWith(shorter, longer);
    }

    [Fact]
    public void TrimToLineCap_KeepsWholeRecordsFromTheTail()
    {
        var line = "{\"n\":" + new string('1', 40) + "}\n";
        var jsonl = line + line + "{\"last\":true}\n";

        var trimmed = NativeTranscript.TrimToLineCap(jsonl, 70);

        Assert.True(trimmed.Length <= 70);
        Assert.StartsWith("{\"", trimmed);
        Assert.EndsWith("{\"last\":true}\n", trimmed);
        Assert.Equal(jsonl, NativeTranscript.TrimToLineCap(jsonl, jsonl.Length));
    }

    [Fact]
    public void EmptyAndGarbageInputProduceNoEntries()
    {
        Assert.Empty(NativeTranscript.Parse(null));
        Assert.Empty(NativeTranscript.Parse(""));
        Assert.Empty(NativeTranscript.Parse("plain terminal text\nwith no json"));
        Assert.Empty(NativeTranscript.Parse("{\"type\":\"user\"}\n{not json"));
    }

    [Fact]
    public void Page_CountsEntriesAndFlagsWhatIsLeft()
    {
        var entries = NativeTranscript.Parse(ClaudeJsonl);

        var first = ConversationPage.FromEntries("s1", "Running", entries, null, 2);
        Assert.Equal(ConversationPage.NativeSource, first.Source);
        Assert.Equal(2, first.Entries.Count);
        Assert.Equal(2, first.NextOffset);
        Assert.Equal(5, first.Length);
        Assert.True(first.Truncated);
        Assert.True(first.Running);

        var rest = ConversationPage.FromEntries("s1", "Succeeded", entries, first.NextOffset, null);
        Assert.Equal(3, rest.Entries.Count);
        Assert.False(rest.Truncated);
        Assert.False(rest.Running);
        Assert.Equal("", rest.Text);

        // A cursor past the end (the poll that finds nothing new) stays where it is.
        var empty = ConversationPage.FromEntries("s1", "Running", entries, 5, null);
        Assert.Empty(empty.Entries);
        Assert.Equal(5, empty.NextOffset);
    }

    [Fact]
    public void Page_FromScrollback_CarriesTheTextPage()
    {
        var page = ConversationPage.FromScrollback("s1", "Running", "abcdef", 2, 3);

        Assert.Equal(ConversationPage.ScrollbackSource, page.Source);
        Assert.Equal("cde", page.Text);
        Assert.Empty(page.Entries);
        Assert.Equal(5, page.NextOffset);
        Assert.Equal(6, page.Length);
        Assert.True(page.Truncated);
    }
}
