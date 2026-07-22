# Telegram & Signal Chat Integration + Slack Fixes — Implementation Plan

> **For Claude:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task.

**Goal:** Telegram and Signal session chat as community features (AGPL core), plus Slack fixes (message splitting, permission expiry), a "working…" indicator, `!status`, and browser desktop notifications.

**Architecture:** Shared chat core in `backend/Chat/` (binding store, formatting, working indicator); per-platform adapters (`Telegram/`, `Signal/`) implementing the existing `INotifier` / `IPermissionNotifier` contracts; Slack (ee) keeps its own store but reuses the shared formatting/animation. Design: `docs/superpowers/specs/2026-07-19-telegram-signal-chat-design.md`.

**Tech Stack:** .NET 8 (Npgsql, HttpClient, ClientWebSocket), Telegram Bot API (long polling), signal-cli-rest-api (json-rpc mode), Vue 3 + vitest, xUnit, Helm.

**Conventions (read first):**
- Community code goes in `backend/` (AGPL) — **no** `IEnterpriseLicense` checks, **no** ee license header. EE files keep their header.
- Never mention any company internals; contact is `open-agenthub@mail.on-mb.com` / GitHub org `open-agenthub`.
- Tests: xUnit in `tests/AgentHub.Api.Tests/`, pure-logic style (see `SlackTargetTests.cs`); frontend vitest colocated in `frontend/src/`.
- Backend tests: `dotnet test tests/AgentHub.Api.Tests` (run from repo root `open-agenthub/`).
- Frontend tests: `cd frontend && npm test` (vitest run).
- Commit after every task.

---

## Phase 1 — Core moves & shared chat utilities

### Task 1: Move `PermissionAction` into the AGPL core

The community permission notifiers need the `perm:<decision>:<id>` encoding; it currently lives in ee.

**Files:**
- Create: `backend/Permissions/PermissionAction.cs`
- Modify: `ee/backend/Slack/SlackPermissionNotifier.cs` (remove the class, add `using AgentHub.Api.Permissions;` — already present)
- Modify: `ee/backend/Slack/SlackSocketModeService.cs` (add `using AgentHub.Api.Permissions;`)
- Modify: `tests/AgentHub.Api.Tests/PermissionActionTests.cs` (using `AgentHub.Api.Permissions`)

**Step 1:** Create `backend/Permissions/PermissionAction.cs` — move the class verbatim from `SlackPermissionNotifier.cs:12-26`, namespace `AgentHub.Api.Permissions`, **no ee header**, plain `// SPDX` style like other core files (match neighboring files — they have no header). Delete the class from `SlackPermissionNotifier.cs`.

**Step 2:** Fix usings in the two ee files and the test file (`using AgentHub.Api.Ee.Slack;` → keep where still needed for other types; add `using AgentHub.Api.Permissions;`).

**Step 3:** Run: `dotnet build backend && dotnet test tests/AgentHub.Api.Tests --filter PermissionAction`
Expected: build OK, 9 tests PASS.

**Step 4:** Commit: `refactor: move PermissionAction to AGPL core`

### Task 2: Move `AgentTerminal` into the AGPL core

**Files:**
- Create: `backend/Services/AgentTerminal.cs` (move from `ee/backend/Slack/AgentTerminal.cs`, namespace `AgentHub.Api.Services`, no ee header; update the XML doc "Slack reply" → "chat reply")
- Delete: `ee/backend/Slack/AgentTerminal.cs`
- Modify: `ee/backend/Slack/SlackSocketModeService.cs` (`AgentTerminal` now resolves via existing `using AgentHub.Api.Services;`)
- Modify: `tests/AgentHub.Api.Tests/SlackAnsiTests.cs` (using → `AgentHub.Api.Services`)

**Steps:** as Task 1. Run: `dotnet build backend && dotnet test tests/AgentHub.Api.Tests`
Expected: all existing tests PASS. Commit: `refactor: move AgentTerminal to AGPL core`

### Task 3: `ChatFormatting` — tags, header, quote, split, status text (TDD)

**Files:**
- Create: `backend/Chat/ChatFormatting.cs`
- Test: `tests/AgentHub.Api.Tests/ChatFormattingTests.cs`

**Step 1: Write the failing tests**

```csharp
using AgentHub.Api.Chat;
using Xunit;

namespace AgentHub.Api.Tests;

public class ChatFormattingTests
{
    [Fact]
    public void Tag_IsFirstFourChars() => Assert.Equal("a3f2", ChatFormatting.Tag("a3f2941be0c1"));

    [Theory]
    [InlineData("a3f2", "a3f2941be0c1", true)]   // exact tag
    [InlineData("a3f29", "a3f2941be0c1", true)]  // longer prefix
    [InlineData("a3", "a3f2941be0c1", true)]     // shorter prefix (caller ensures uniqueness)
    [InlineData("b7c1", "a3f2941be0c1", false)]
    [InlineData("", "a3f2941be0c1", false)]
    public void MatchesTag(string tag, string sessionId, bool expected)
        => Assert.Equal(expected, ChatFormatting.MatchesTag(tag, sessionId));

    [Fact]
    public void Split_ShortText_SingleChunk()
        => Assert.Equal(new[] { "hi" }, ChatFormatting.Split("hi", 100));

    [Fact]
    public void Split_BreaksAtLineBoundaries()
    {
        var text = string.Join("\n", Enumerable.Repeat("0123456789", 5)); // 54 chars
        var chunks = ChatFormatting.Split(text, 25);
        Assert.All(chunks, c => Assert.True(c.Length <= 25));
        Assert.Equal(text, string.Join("\n", chunks)); // lossless
        Assert.Equal(new[] { "0123456789\n0123456789", "0123456789\n0123456789", "0123456789" }, chunks);
    }

    [Fact]
    public void Split_HardSplitsOverlongSingleLine()
    {
        var chunks = ChatFormatting.Split(new string('x', 60), 25);
        Assert.Equal(3, chunks.Count);
        Assert.All(chunks, c => Assert.True(c.Length <= 25));
        Assert.Equal(new string('x', 60), string.Concat(chunks));
    }

    [Fact]
    public void Split_EmptyText_NoChunks() => Assert.Empty(ChatFormatting.Split("", 100));

    [Fact]
    public void Header_ContainsTagAndTitle()
    {
        var h = ChatFormatting.Header("a3f2941be0c1", "fix-login");
        Assert.Contains("#a3f2", h);
        Assert.Contains("fix-login", h);
    }

    [Fact]
    public void StatusText_MentionsPhaseAndPending()
    {
        var s = ChatFormatting.StatusText("Running", questionPending: true, pendingTool: "Bash", "https://x/s/1");
        Assert.Contains("Running", s);
        Assert.Contains("Bash", s);
        Assert.Contains("https://x/s/1", s);
    }
}
```

**Step 2:** Run: `dotnet test tests/AgentHub.Api.Tests --filter ChatFormatting` — Expected: FAIL (type missing).

**Step 3: Implement `backend/Chat/ChatFormatting.cs`**

```csharp
namespace AgentHub.Api.Chat;

/// <summary>Platform-neutral chat text helpers: session tags, headers, splitting.</summary>
public static class ChatFormatting
{
    /// <summary>Short session tag shown in chat (first 4 chars of the session id).</summary>
    public static string Tag(string sessionId) => sessionId.Length <= 4 ? sessionId : sessionId[..4];

    /// <summary>True when <paramref name="tag"/> is a non-empty prefix of the session id.</summary>
    public static bool MatchesTag(string tag, string sessionId)
        => tag.Length > 0 && sessionId.StartsWith(tag, StringComparison.OrdinalIgnoreCase);

    public static string Header(string sessionId, string title) => $"🤖 #{Tag(sessionId)} · {title}";

    /// <summary>
    /// Splits text into chunks of at most maxLen, preferring line boundaries; a single
    /// line longer than maxLen is hard-split. Joining chunks with "\n" (or "" for
    /// hard-split-only input) reproduces the original text.
    /// </summary>
    public static IReadOnlyList<string> Split(string text, int maxLen)
    {
        var chunks = new List<string>();
        if (string.IsNullOrEmpty(text)) return chunks;
        var current = new System.Text.StringBuilder();
        foreach (var rawLine in text.Split('\n'))
        {
            var line = rawLine;
            while (line.Length > maxLen) // hard split an overlong line
            {
                if (current.Length > 0) { chunks.Add(current.ToString()); current.Clear(); }
                chunks.Add(line[..maxLen]);
                line = line[maxLen..];
            }
            if (current.Length + line.Length + 1 > maxLen && current.Length > 0)
            { chunks.Add(current.ToString()); current.Clear(); }
            if (current.Length > 0) current.Append('\n');
            current.Append(line);
        }
        if (current.Length > 0) chunks.Add(current.ToString());
        return chunks;
    }

    public static string StatusText(string phase, bool questionPending, string? pendingTool, string? link)
    {
        var lines = new List<string> { $"Status: {phase}" };
        if (questionPending) lines.Add("💬 Waiting for your reply.");
        if (pendingTool is not null) lines.Add($"🔒 Permission pending: {pendingTool}");
        if (!questionPending && pendingTool is null && phase == "Running") lines.Add("⏳ Claude is working.");
        if (!string.IsNullOrEmpty(link)) lines.Add(link);
        return string.Join("\n", lines);
    }
}
```

Note: the `Split` line-boundary test above pins exact chunking — if your implementation groups differently but stays lossless and ≤ maxLen, fix the implementation, not the test, until this exact output matches (deterministic greedy grouping).

**Step 4:** Run the filter again — Expected: PASS. Then full suite: `dotnet test tests/AgentHub.Api.Tests` — PASS.

**Step 5:** Commit: `feat: shared chat formatting (tags, split, status text)`

### Task 4: `ChatBindingStore` (Postgres)

Pattern: copy the style of `ee/backend/Slack/SlackThreadStore.cs` (NpgsqlDataSource from `ConnectionStrings:Postgres`). No DB tests (matches repo convention — stores are exercised in deployment).

**Files:**
- Create: `backend/Chat/ChatBindingStore.cs`
- Modify: `backend/Program.cs` (register singleton + `InitializeAsync` in the init scope, next to `SlackThreadStore`)

**Step 1: Implement**

```csharp
using Npgsql;

namespace AgentHub.Api.Chat;

/// <summary>A session's conversation on one chat platform ("telegram" | "signal").</summary>
public sealed record ChatBinding(
    string Platform, string SessionId, string Owner, string ChatId,
    string? ThreadId,      // Telegram forum topic id; null for DMs/Signal
    string? StatusRef,     // message id/timestamp of the "working…" indicator
    bool Active);          // the chat's current default session (plain replies go here)

public sealed class ChatBindingStore
{
    private readonly NpgsqlDataSource _db;

    public ChatBindingStore(IConfiguration cfg)
    {
        var cs = cfg.GetConnectionString("Postgres")
                 ?? throw new InvalidOperationException("ConnectionStrings:Postgres is missing.");
        _db = NpgsqlDataSource.Create(cs);
    }

    public async Task InitializeAsync(CancellationToken ct = default)
    {
        const string ddl = """
            CREATE TABLE IF NOT EXISTS chat_session_bindings (
                platform    TEXT NOT NULL,
                session_id  TEXT NOT NULL,
                owner       TEXT NOT NULL,
                chat_id     TEXT NOT NULL,
                thread_id   TEXT,
                status_ref  TEXT,
                active      BOOLEAN NOT NULL DEFAULT FALSE,
                PRIMARY KEY (platform, session_id)
            );
            CREATE INDEX IF NOT EXISTS idx_chat_bindings_chat ON chat_session_bindings(platform, chat_id);
            CREATE TABLE IF NOT EXISTS chat_messages (
                platform    TEXT NOT NULL,
                chat_id     TEXT NOT NULL,
                message_ref TEXT NOT NULL,
                session_id  TEXT NOT NULL,
                created_at  TIMESTAMPTZ NOT NULL DEFAULT now(),
                PRIMARY KEY (platform, chat_id, message_ref)
            );
            """;
        await using var cmd = _db.CreateCommand(ddl);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task UpsertAsync(ChatBinding b, CancellationToken ct = default) { /* INSERT ... ON CONFLICT (platform, session_id) DO UPDATE — all columns, style of SlackThreadStore.UpsertAsync */ }

    public async Task<ChatBinding?> GetAsync(string platform, string sessionId, CancellationToken ct = default) { /* SELECT WHERE platform+session_id */ }

    /// <summary>Binding for a Telegram forum topic (platform + chat + thread).</summary>
    public async Task<ChatBinding?> GetByThreadAsync(string platform, string chatId, string threadId, CancellationToken ct = default) { }

    /// <summary>The chat's active session (plain messages without reply go here).</summary>
    public async Task<ChatBinding?> GetActiveAsync(string platform, string chatId, CancellationToken ct = default) { /* WHERE active LIMIT 1 */ }

    /// <summary>Marks one session active per chat (clears the flag on all others of that chat).</summary>
    public async Task SetActiveAsync(string platform, string chatId, string sessionId, CancellationToken ct = default)
    { /* single command: UPDATE chat_session_bindings SET active = (session_id = @sid) WHERE platform=@p AND chat_id=@c */ }

    /// <summary>All bindings of a chat, most recently created session first (for !sessions). Join not needed — return bindings; the caller enriches.</summary>
    public async Task<IReadOnlyList<ChatBinding>> ListByChatAsync(string platform, string chatId, CancellationToken ct = default) { }

    public async Task SetStatusRefAsync(string platform, string sessionId, string? statusRef, CancellationToken ct = default) { }

    public async Task RecordMessageAsync(string platform, string chatId, string messageRef, string sessionId, CancellationToken ct = default)
    { /* INSERT ... ON CONFLICT DO NOTHING */ }

    public async Task<string?> GetSessionByMessageAsync(string platform, string chatId, string messageRef, CancellationToken ct = default) { }

    /// <summary>Prunes reply-routing rows older than 30 days. Called daily by the poll services.</summary>
    public async Task PruneMessagesAsync(CancellationToken ct = default)
    { /* DELETE FROM chat_messages WHERE created_at < now() - interval '30 days' */ }
}
```

Fill in the elided bodies exactly in the style of `SlackThreadStore` (parameterized commands, `ExecuteReaderAsync`, record mapping).

**Step 2:** In `Program.cs`: `builder.Services.AddSingleton<AgentHub.Api.Chat.ChatBindingStore>();` (with the other singletons) and in the init scope: `await scope.ServiceProvider.GetRequiredService<AgentHub.Api.Chat.ChatBindingStore>().InitializeAsync();`

**Step 3:** `dotnet build backend` — OK. Commit: `feat: chat binding store for community chat platforms`

---

## Phase 2 — Slack fixes (immediately useful, shared pieces fall out)

### Task 5: Split long answers instead of truncating

**Files:**
- Modify: `agent-runtime/session-agent/notify-hook.sh:24` — `msg.slice(0, 1500)` → `msg.slice(0, 12000)`
- Modify: `ee/backend/Slack/SlackNotifier.cs` — replace `Quote()`'s 2500-char cut with `ChatFormatting.Split`

**Step 1:** notify-hook change (one number).

**Step 2:** In `SlackNotifier.NotifyAsync`, replace the single `PostMessageAsync(..., Quote(message), ...)` call (line 74) with:

```csharp
var chunks = AgentHub.Api.Chat.ChatFormatting.Split(Escape(message.Trim()), 3800);
for (var i = 0; i < chunks.Count; i++)
{
    var quoted = string.Join("\n", chunks[i].Split('\n').Select(l => "> " + l));
    var label = i == 0 ? ":speech_balloon: *The agent says:*" : $"_… ({i + 1}/{chunks.Count})_";
    await _slack.PostMessageAsync(thread.Channel, label + "\n" + quoted, thread.ThreadTs, ct);
}
```

Delete the now-unused `Quote()` helper. Keep `Escape()`.

**Step 3:** `dotnet build backend && dotnet test tests/AgentHub.Api.Tests` — PASS.
**Step 4:** Commit: `fix: split long agent answers across chat messages instead of truncating`

### Task 6: Permission expiry — store, endpoint, hook, Slack message update

**Files:**
- Modify: `backend/Permissions/PermissionStore.cs` (platform column, `GetAsync`, rename `SetSlackMessageAsync`)
- Modify: `backend/Controllers/InternalController.cs` (expire endpoint, `IEnumerable<IPermissionPromptEditor>`)
- Create: `backend/Permissions/IPermissionPromptEditor.cs`
- Modify: `ee/backend/Slack/SlackPermissionNotifier.cs` (call renamed store method; implement editor)
- Modify: `ee/backend/Slack/SlackSocketModeService.cs` (late-click feedback)
- Modify: `backend/Program.cs` (register editor)
- Modify: `agent-runtime/session-agent/pretooluse-hook.sh` (configurable poll + expire call)
- Modify: `agent-runtime/session-agent/mcp-policy-hook.sh` (settings timeouts 300 → 1900)
- Modify: `agent-runtime/session-agent/test/mcp-policy-hook.test.js` (timeout expectations)
- Modify: `helm/open-agenthub/templates/backend.yaml` or the session-pod env source — see step 5

**Step 1: Store changes.** In `PermissionStore`:
- DDL: add `ALTER TABLE permission_requests ADD COLUMN IF NOT EXISTS platform TEXT;` to `InitializeAsync`.
- `PermissionRequest`: add `public string? Platform { get; set; }`.
- Rename `SetSlackMessageAsync` → `SetPromptMessageAsync(string id, string platform, string channel, string messageRef, ...)`, SQL sets `platform=@p, channel=@c, message_ts=@m`. Update the Slack caller (`SlackPermissionNotifier.cs:82`) to pass `"slack"`.
- Add `GetAsync(string id)` returning the full row (same mapping as `ResolveAsync`, plus `platform`), and include `platform` in `ResolveAsync`'s RETURNING + mapping.

**Step 2: Editor interface + endpoint.**

`backend/Permissions/IPermissionPromptEditor.cs`:

```csharp
namespace AgentHub.Api.Permissions;

/// <summary>Rewrites an out-of-band permission prompt once it can no longer be answered
/// (expired) — e.g. removes the buttons and says "answer in the web terminal".</summary>
public interface IPermissionPromptEditor
{
    string Platform { get; }
    Task MarkExpiredAsync(PermissionRequest request, CancellationToken ct = default);
}
```

`InternalController`: inject `IEnumerable<IPermissionPromptEditor> promptEditors`; add:

```csharp
/// <summary>The hook gave up waiting: mark the request expired and defuse the chat prompt.</summary>
[HttpPost("permission/{reqId}/expire")]
public async Task<IActionResult> ExpirePermission(string id, string reqId, CancellationToken ct)
{
    if (await AuthAsync(id, ct) is null) return Unauthorized();
    var resolved = await _permissions.ResolveAsync(reqId, "expired", ct);
    if (resolved?.Platform is { } platform)
        foreach (var e in _promptEditors.Where(e => e.Platform == platform))
            await e.MarkExpiredAsync(resolved, ct);
    return NoContent();
}
```

**Step 3: Slack editor + late-click feedback.** `SlackPermissionNotifier` additionally implements `IPermissionPromptEditor` (`Platform => "slack"`):

```csharp
public async Task MarkExpiredAsync(PermissionRequest req, CancellationToken ct = default)
{
    if (req.Channel is null || req.MessageTs is null) return;
    await _slack.UpdateMessageAsync(req.Channel, req.MessageTs,
        $":hourglass: *Expired* — *{Escape(req.Tool)}*. Please answer in the web terminal.", null, ct);
}
```

Register in `Program.cs`: `builder.Services.AddSingleton<AgentHub.Api.Permissions.IPermissionPromptEditor>(sp => (AgentHub.Api.Ee.Slack.SlackPermissionNotifier)sp.GetRequiredService<AgentHub.Api.Permissions.IPermissionNotifier>());` — simpler: register the class once as singleton and alias both interfaces to it (pattern used for `LicenseStore` at `Program.cs:36-38`).

In `SlackSocketModeService.HandleInteractiveAsync`, when `ResolveAsync` returns null (line 135), fetch the request and reflect its final state instead of returning silently:

```csharp
if (resolved is null)
{
    var existing = await _permissions.GetAsync(reqId, ct);
    if (existing is { Channel: not null, MessageTs: not null })
        await _slack.UpdateMessageAsync(existing.Channel, existing.MessageTs,
            existing.Decision == "expired"
                ? $":hourglass: *Expired* — *{existing.Tool}*. Please answer in the web terminal."
                : $":information_source: Already decided ({existing.Decision}) — *{existing.Tool}*.", null, ct);
    return;
}
```

**Step 4: Hook.** Replace the poll loop in `pretooluse-hook.sh:34-40` with:

```bash
# Poll for the decision; configurable window (default 30 min). The hook 'timeout'
# in settings.json (1900s) is the hard cap.
poll="${AGENTHUB_PERMISSION_POLL_SECONDS:-1800}"
elapsed=0
while [ "$elapsed" -lt "$poll" ]; do
  sleep 2; elapsed=$((elapsed + 2))
  dec="$(curl -fsS -H "X-Agent-Token: $AGENTHUB_CALLBACK_TOKEN" "$AGENTHUB_CALLBACK_URL/permission/$id" 2>/dev/null | field decision)"
  [ -n "$dec" ] && [ "$dec" != "pending" ] && emit "$dec"
done
# Gave up: tell the backend so it defuses the chat buttons, then fall back to "ask".
curl -fsS -X POST -H "X-Agent-Token: $AGENTHUB_CALLBACK_TOKEN" \
  "$AGENTHUB_CALLBACK_URL/permission/$id/expire" >/dev/null 2>&1 || true
emit ask
```

(`emit`'s case already maps `expired` → `ask`.)

**Step 5: Hook timeouts.** In `mcp-policy-hook.sh` interactive settings (lines 20-21): `"timeout": 300` → `"timeout": 1900` on **both** PreToolUse entries. Update the matching assertions in `agent-runtime/session-agent/test/mcp-policy-hook.test.js` (search for `300`). `AGENTHUB_PERMISSION_POLL_SECONDS` is optional (default in-script); expose it later via Helm only if someone asks (YAGNI).

**Step 6:** Run: `dotnet build backend && dotnet test tests/AgentHub.Api.Tests` and `cd agent-runtime/session-agent && node --test test/`
Expected: PASS (adjusted timeout assertions included).

**Step 7:** Commit: `fix: permission prompts expire visibly instead of dying silently (30 min window)`

### Task 7: Working indicator — shared animation + Slack wiring

**Files:**
- Create: `backend/Chat/WorkingIndicator.cs`
- Test: `tests/AgentHub.Api.Tests/WorkingIndicatorTests.cs`
- Modify: `ee/backend/Slack/SlackThreadStore.cs` (status_ts column + setter)
- Modify: `ee/backend/Slack/SlackClient.cs` (`DeleteMessageAsync` via chat.delete)
- Modify: `ee/backend/Slack/SlackSocketModeService.cs` (start indicator after delivering input)
- Modify: `ee/backend/Slack/SlackNotifier.cs` (stop/delete indicator on any event)
- Modify: `backend/Program.cs` (register `WorkingIndicator` singleton)

**Step 1: Failing test** (`WorkingIndicatorTests.cs`):

```csharp
using AgentHub.Api.Chat;
using Xunit;

namespace AgentHub.Api.Tests;

public class WorkingIndicatorTests
{
    [Fact]
    public async Task Animates_UntilStopped()
    {
        var frames = new List<string>();
        var wi = new WorkingIndicator(interval: TimeSpan.FromMilliseconds(10), maxDuration: TimeSpan.FromSeconds(5));
        wi.Start("s1", async (text, ct) => { lock (frames) frames.Add(text); });
        await Task.Delay(100);
        wi.Stop("s1");
        var count = frames.Count;
        Assert.True(count >= 2, $"expected ≥2 frames, got {count}");
        await Task.Delay(60);
        Assert.Equal(count, frames.Count); // no frames after Stop
    }

    [Fact]
    public void Start_ReplacesExistingLoop()
    {
        var wi = new WorkingIndicator(TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(1));
        wi.Start("s1", (_, _) => Task.CompletedTask);
        wi.Start("s1", (_, _) => Task.CompletedTask); // must not throw or leak
        wi.Stop("s1");
        wi.Stop("s1"); // idempotent
    }
}
```

**Step 2:** Run filter `WorkingIndicator` — FAIL.

**Step 3: Implement** `backend/Chat/WorkingIndicator.cs`:

```csharp
using System.Collections.Concurrent;

namespace AgentHub.Api.Chat;

/// <summary>
/// In-memory "Claude is working…" animator: per session one loop that invokes an
/// edit callback with the next frame until stopped or maxDuration elapses. The
/// message itself (creation/deletion) is owned by the platform adapters; deletion
/// works cross-replica via the persisted status message ref.
/// </summary>
public sealed class WorkingIndicator
{
    public static readonly string[] Frames =
        { "⏳ Claude is working …", "⌛ Claude is working ‥", "⏳ Claude is working .", "⌛ Claude is working ‥" };

    private readonly TimeSpan _interval, _maxDuration;
    private readonly ConcurrentDictionary<string, CancellationTokenSource> _loops = new();

    public WorkingIndicator() : this(TimeSpan.FromSeconds(5), TimeSpan.FromMinutes(30)) { }
    public WorkingIndicator(TimeSpan interval, TimeSpan maxDuration) { _interval = interval; _maxDuration = maxDuration; }

    public void Start(string sessionId, Func<string, CancellationToken, Task> edit)
    {
        var cts = new CancellationTokenSource(_maxDuration);
        var old = _loops.GetOrAdd(sessionId, cts);
        if (!ReferenceEquals(old, cts)) { Stop(sessionId); _loops[sessionId] = cts; }
        _ = Task.Run(async () =>
        {
            var i = 0;
            try
            {
                while (!cts.Token.IsCancellationRequested)
                {
                    await Task.Delay(_interval, cts.Token);
                    await edit(Frames[++i % Frames.Length], cts.Token);
                }
            }
            catch { /* cancelled or edit failed — stop quietly */ }
        });
    }

    public void Stop(string sessionId)
    {
        if (_loops.TryRemove(sessionId, out var cts)) { cts.Cancel(); cts.Dispose(); }
    }
}
```

**Step 4:** Filter PASS, full suite PASS.

**Step 5: Slack wiring.**
- `SlackThreadStore.InitializeAsync` DDL: append `ALTER TABLE slack_threads ADD COLUMN IF NOT EXISTS status_ts TEXT;`; extend record `SlackThread` with `string? StatusTs = null` (trailing optional param keeps callers compiling); include in `QueryOne`/`UpsertAsync`; add `SetStatusTsAsync(string sessionId, string? ts)`.
- `SlackClient.DeleteMessageAsync(channel, ts, ct)` — POST `https://slack.com/api/chat.delete` `{channel, ts}`, log-and-swallow errors like `UpdateMessageAsync`.
- `SlackSocketModeService.HandleEventAsync` — after `AgentTerminal.SendInputAsync` (line 117):

```csharp
var statusTs = await _slack.PostMessageAsync(thread.Channel, WorkingIndicator.Frames[0], threadTs, ct);
if (statusTs is not null)
{
    await _threads.SetStatusTsAsync(thread.SessionId, statusTs, ct);
    _indicator.Start(thread.SessionId, (text, c) => _slack.UpdateMessageAsync(thread.Channel, statusTs, text, null, c));
}
```

(inject `WorkingIndicator _indicator` via ctor)
- `SlackNotifier.NotifyAsync` — first thing after loading `thread` (any event type): 

```csharp
_indicator.Stop(s.Id);
if (thread?.StatusTs is { } sts)
{
    await _slack.DeleteMessageAsync(thread.Channel, sts, ct);
    await _threads.SetStatusTsAsync(s.Id, null, ct);
}
```

- `Program.cs`: `builder.Services.AddSingleton<AgentHub.Api.Chat.WorkingIndicator>();`

**Step 6:** Build + full test suite — PASS. Commit: `feat: "Claude is working…" indicator in Slack threads`

### Task 8: `!status` in Slack threads

**Files:**
- Modify: `backend/Permissions/PermissionStore.cs` (add `GetPendingBySessionAsync`)
- Modify: `ee/backend/Slack/SlackSocketModeService.cs`

**Step 1:** `PermissionStore.GetPendingBySessionAsync(string sessionId)` → `SELECT tool FROM permission_requests WHERE session_id=@s AND decision IS NULL ORDER BY created_at DESC LIMIT 1` returning `string?`.

**Step 2:** In `HandleEventAsync`, after resolving `thread` (line 108) and **before** the pod-IP check, intercept the keyword:

```csharp
if (textReply.Trim().Equals("!status", StringComparison.OrdinalIgnoreCase))
{
    var live = await _sessions.GetSessionAsync(thread.Owner, thread.SessionId, ct);
    var rec = await _store.GetAsync(thread.Owner, thread.SessionId, ct); // ISessionStore — questionPending flag
    var pendingTool = await _permissions.GetPendingBySessionAsync(thread.SessionId, ct);
    var link = string.IsNullOrEmpty(_frontendOrigin) ? null : $"{_frontendOrigin}/s/{thread.SessionId}";
    await _slack.PostMessageAsync(thread.Channel,
        AgentHub.Api.Chat.ChatFormatting.StatusText(live?.Phase ?? "Unknown", rec?.QuestionPending ?? false, pendingTool, link),
        threadTs, ct);
    return;
}
```

Check `ISessionStore` for the exact getter/record shape (`backend/Persistence/`) — use whatever exposes `QuestionPending`; inject `ISessionStore` and `IConfiguration`-derived `_frontendOrigin` (copy the `FrontendOrigin` read from `SlackNotifier`).

**Step 3:** Build + tests PASS. Commit: `feat: !status query in Slack session threads`

---

## Phase 3 — Telegram

### Task 9: Options, user-directory fields, config flag

**Files:**
- Create: `backend/Chat/Telegram/TelegramOptions.cs`
- Modify: `backend/Persistence/UserDirectory.cs`
- Modify: `backend/Program.cs`, `backend/appsettings.json`
- Modify: `tests/AgentHub.Api.Tests/SlackTargetTests.cs` (AppUser ctor grows)

**Step 1:** `TelegramOptions` (section `Chat:Telegram`): `Enabled`, `BotToken`, `bool CanRun => Enabled && !string.IsNullOrWhiteSpace(BotToken);`. Bind + register in `Program.cs` (pattern of `slackOpts`, line 45). Add `"Chat": { "Telegram": { "Enabled": false, "BotToken": "" }, "Signal": { "Enabled": false, "ApiUrl": "", "Number": "" } }` to `appsettings.json`.

**Step 2:** `UserDirectory`:
- DDL migrations: `ALTER TABLE app_users ADD COLUMN IF NOT EXISTS telegram_chat_id TEXT; … telegram_forum BOOLEAN NOT NULL DEFAULT FALSE; … telegram_enabled BOOLEAN NOT NULL DEFAULT TRUE; … signal_number TEXT; … signal_verified BOOLEAN NOT NULL DEFAULT FALSE; … signal_enabled BOOLEAN NOT NULL DEFAULT TRUE;`
- Extend `AppUser` with trailing optional params: `string? TelegramChatId = null, bool TelegramForum = false, bool TelegramEnabled = true, string? SignalNumber = null, bool SignalVerified = false, bool SignalEnabled = true` (optional ⇒ `SlackTargetTests` helper keeps compiling; still re-run it).
- Update `GetAsync` select/mapping; add `SetTelegramLinkAsync(owner, chatId, isForum)`, `ClearTelegramLinkAsync(owner)`, `SetTelegramEnabledAsync(owner, bool)`, `SetSignalNumberAsync(owner, number)` (sets `signal_verified=false`), `SetSignalVerifiedAsync(owner, bool)`, `SetSignalEnabledAsync(owner, bool)`, `GetByTelegramChatAsync(chatId)` (`AppUser?`).

**Step 3:** `/api/config` (Program.cs:159): add `telegramEnabled = telegramOpts.Enabled, signalEnabled = signalOpts.Enabled` (signal opts arrive in Task 13; for now add telegram only, signal in Task 13).

**Step 4:** Build + full tests PASS. Commit: `feat: telegram options + per-user chat fields`

### Task 10: `TelegramClient` + update parser (TDD for parser)

**Files:**
- Create: `backend/Chat/Telegram/TelegramClient.cs`
- Create: `backend/Chat/Telegram/TelegramUpdate.cs` (pure parser)
- Test: `tests/AgentHub.Api.Tests/TelegramUpdateTests.cs`

**Step 1: Failing parser tests** — parse real Bot API update JSON shapes:

```csharp
using AgentHub.Api.Chat.Telegram;
using Xunit;

namespace AgentHub.Api.Tests;

public class TelegramUpdateTests
{
    [Fact]
    public void ParsesPlainMessage()
    {
        var u = TelegramUpdate.Parse("""
            {"update_id":1,"message":{"message_id":10,"chat":{"id":-100123,"type":"supergroup","is_forum":true},
             "from":{"id":42,"is_bot":false,"username":"maik"},"text":"hello","message_thread_id":77}}
            """);
        Assert.Equal(TelegramUpdateKind.Message, u!.Kind);
        Assert.Equal("-100123", u.ChatId);
        Assert.Equal("77", u.ThreadId);
        Assert.Equal("hello", u.Text);
        Assert.True(u.IsForumChat);
        Assert.Null(u.ReplyToMessageId);
    }

    [Fact]
    public void ParsesReply()
    {
        var u = TelegramUpdate.Parse("""
            {"update_id":2,"message":{"message_id":11,"chat":{"id":5,"type":"private"},
             "from":{"id":42,"is_bot":false},"text":"yes","reply_to_message":{"message_id":9}}}
            """);
        Assert.Equal("9", u!.ReplyToMessageId);
        Assert.Null(u.ThreadId);
    }

    [Fact]
    public void ParsesCallbackQuery()
    {
        var u = TelegramUpdate.Parse("""
            {"update_id":3,"callback_query":{"id":"cb1","from":{"id":42,"username":"maik"},
             "data":"perm:allow:abc","message":{"message_id":12,"chat":{"id":5,"type":"private"}}}}
            """);
        Assert.Equal(TelegramUpdateKind.Callback, u!.Kind);
        Assert.Equal("perm:allow:abc", u.CallbackData);
        Assert.Equal("cb1", u.CallbackId);
        Assert.Equal("12", u.MessageId);
    }

    [Fact]
    public void IgnoresBotAndEmpty()
    {
        Assert.Null(TelegramUpdate.Parse("""{"update_id":4,"message":{"message_id":1,"chat":{"id":5,"type":"private"},"from":{"id":1,"is_bot":true},"text":"x"}}"""));
        Assert.Null(TelegramUpdate.Parse("""{"update_id":5,"edited_message":{}}"""));
        Assert.Null(TelegramUpdate.Parse("not json"));
    }

    [Fact]
    public void ParsesForumTopicReplyFallback()
    {
        // In forum topics every message carries message_thread_id AND reply_to_message
        // (pointing at the topic-creation service message) — thread id must win, reply ignored.
        var u = TelegramUpdate.Parse("""
            {"update_id":6,"message":{"message_id":13,"chat":{"id":-100123,"type":"supergroup","is_forum":true},
             "from":{"id":42,"is_bot":false},"text":"t","message_thread_id":77,"reply_to_message":{"message_id":77}}}
            """);
        Assert.Equal("77", u!.ThreadId);
    }
}
```

**Step 2:** Run filter — FAIL. **Step 3: Implement**

`TelegramUpdate.cs`: `enum TelegramUpdateKind { Message, Callback }` and a sealed record with `Kind, UpdateId(long), ChatId, ThreadId, MessageId, Text, ReplyToMessageId, IsForumChat, FromUsername, CallbackData, CallbackId`; static `Parse(string json)` → null for bot senders, edited messages, unparseable input. Note: when `reply_to_message.message_id == message_thread_id` (topic-creation service message) set `ReplyToMessageId = null`.

`TelegramClient.cs` — thin wrapper (style of `SlackClient`, `IHttpClientFactory`, base `https://api.telegram.org/bot{token}/`):
- `Task<string?> SendMessageAsync(string chatId, string text, string? threadId, object? replyMarkup, CancellationToken ct)` → POST `sendMessage` `{chat_id, text, message_thread_id?, reply_markup?, disable_web_page_preview:true}`; returns `result.message_id` as string, null on failure (log warning with `description`).
- `Task EditMessageTextAsync(string chatId, string messageId, string text, object? replyMarkup, CancellationToken ct)` → `editMessageText`.
- `Task DeleteMessageAsync(string chatId, string messageId, CancellationToken ct)` → `deleteMessage`.
- `Task<string?> CreateForumTopicAsync(string chatId, string name, CancellationToken ct)` → `createForumTopic`, returns `result.message_thread_id`.
- `Task AnswerCallbackAsync(string callbackId, string? text, CancellationToken ct)` → `answerCallbackQuery`.
- `Task<(long nextOffset, List<string> updates)> GetUpdatesAsync(long offset, CancellationToken ct)` → GET `getUpdates?offset={o}&timeout=50&allowed_updates=["message","callback_query"]` with an `HttpClient` timeout > 50 s (create the client with `Timeout = TimeSpan.FromSeconds(70)`); returns raw JSON strings of `result[]` plus max(update_id)+1.
- `Task<string?> GetBotUsernameAsync(CancellationToken ct)` → `getMe`, cache the result in a field.

All Telegram messages are sent as plain text (no parse_mode) — avoids Markdown-escaping bugs with code/paths in agent output. Emojis carry the formatting.

**Step 4:** Filter + full suite PASS. Commit: `feat: telegram client and update parser`

### Task 11: Link codes + `ChatController` + notifier

**Files:**
- Create: `backend/Chat/ChatLinkCodeStore.cs`
- Create: `backend/Controllers/ChatController.cs`
- Create: `backend/Chat/Telegram/TelegramNotifier.cs`
- Modify: `backend/Program.cs`

**Step 1:** `ChatLinkCodeStore` (Postgres, style as always): table `chat_link_codes(code TEXT PRIMARY KEY, owner TEXT NOT NULL, purpose TEXT NOT NULL, payload TEXT, created_at TIMESTAMPTZ DEFAULT now())`. Methods: `CreateAsync(owner, purpose, payload?)` → generates code (`Guid.NewGuid().ToString("n")[..8]` for telegram links; `Random.Shared.Next(100000,1000000).ToString()` for signal verify), upserts (one code per owner+purpose: delete old first), returns code; `ConsumeAsync(code, purpose)` → returns `(owner, payload)?` if found **and younger than 10 minutes**, deletes the row (also delete when expired). Init in Program.cs scope.

**Step 2:** `ChatController` (`[Authorize] api/chat`, Owner claim pattern from `SlackController`):

```csharp
public sealed record ChatMe(TelegramMe Telegram, SignalMe Signal);
public sealed record TelegramMe(bool Configured, bool Linked, bool Forum, bool Enabled);
public sealed record SignalMe(bool Configured, string? Number, bool Verified, bool Enabled);

[HttpGet("me")]  // assemble from UserDirectory + options ("Configured" = platform enabled on the instance)
[HttpPost("telegram/link-code")]  // create code, return { code, botUsername, deepLink: $"https://t.me/{bot}?start={code}" }
[HttpPut("telegram")]   // body {enabled} → SetTelegramEnabledAsync
[HttpDelete("telegram")] // ClearTelegramLinkAsync
[HttpPut("signal")]     // body {enabled, number?} — Task 13 fills in the verify-send
[HttpPost("signal/verify")] // body {code} — Task 13
```

Signal endpoints: create them now returning `501 NotImplemented` if `SignalOptions` is absent — or simply add them in Task 13; choose adding in Task 13 (YAGNI now).

**Step 3:** `TelegramNotifier : INotifier` — mirrors `SlackNotifier` flow, no license check:

```csharp
public async Task NotifyAsync(SessionRecord s, string eventType, string message, CancellationToken ct = default)
{
    if (!_opts.CanRun) return;
    try
    {
        var binding = await _bindings.GetAsync("telegram", s.Id, ct);
        _indicator.Stop(s.Id);                               // working indicator: stop + delete
        if (binding?.StatusRef is { } sref)
        {
            await _tg.DeleteMessageAsync(binding.ChatId, sref, ct);
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
            var user = await _users.GetAsync(s.Owner, ct);
            if (user is not { TelegramEnabled: true, TelegramChatId: not null }) return;
            string? threadId = null;
            if (user.TelegramForum)
                threadId = await _tg.CreateForumTopicAsync(user.TelegramChatId, $"{s.Title} #{ChatFormatting.Tag(s.Id)}", ct);
            var header = ChatFormatting.Header(s.Id, s.Title) + $" ({s.Mode})\n" +
                         (string.IsNullOrEmpty(_frontendOrigin) ? "" : $"{_frontendOrigin}/s/{s.Id}\n") +
                         (threadId is null
                            ? "Reply to a message of this session (or /use " + ChatFormatting.Tag(s.Id) + ") to answer. !status shows progress."
                            : "Reply in this topic to answer. !status shows progress.");
            var headerId = await _tg.SendMessageAsync(user.TelegramChatId, header, threadId, null, ct);
            if (headerId is null) return;
            binding = new ChatBinding("telegram", s.Id, s.Owner, user.TelegramChatId, threadId, null, false);
            await _bindings.UpsertAsync(binding, ct);
            await _bindings.RecordMessageAsync("telegram", binding.ChatId, headerId, s.Id, ct);
        }

        // The most recent question owns the chat's plain replies (DM mode).
        await _bindings.SetActiveAsync("telegram", binding.ChatId, s.Id, ct);

        var chunks = ChatFormatting.Split(message.Trim(), 4000);
        for (var i = 0; i < chunks.Count; i++)
        {
            var label = i == 0 ? "💬 The agent says:\n" : $"… ({i + 1}/{chunks.Count})\n";
            var mid = await _tg.SendMessageAsync(binding.ChatId, label + chunks[i], binding.ThreadId, null, ct);
            if (mid is not null) await _bindings.RecordMessageAsync("telegram", binding.ChatId, mid, s.Id, ct);
        }
    }
    catch (Exception ex) { _log.LogWarning(ex, "Telegram notify failed for session {Id}", s.Id); }
}
```

**Step 4:** Register in `Program.cs`: options, `TelegramClient`, `ChatLinkCodeStore` (+ init), `builder.Services.AddSingleton<AgentHub.Api.Notifications.INotifier, AgentHub.Api.Chat.Telegram.TelegramNotifier>();`

**Step 5:** Build + tests PASS. Commit: `feat: telegram notifier, link codes, chat settings API`

### Task 12: `TelegramUpdateService` + permission buttons

**Files:**
- Create: `backend/Chat/Telegram/TelegramUpdateService.cs` (BackgroundService)
- Create: `backend/Chat/Telegram/TelegramPermissionNotifier.cs`
- Modify: `backend/Program.cs`

**Step 1:** `TelegramPermissionNotifier : IPermissionNotifier, IPermissionPromptEditor` (`Platform => "telegram"`). `PostAsync`: skip unless `_opts.CanRun`; target = session binding (`GetAsync("telegram", req.SessionId)`) else owner's `TelegramChatId` (respecting `TelegramEnabled`); return false when neither. Inline keyboard:

```csharp
var markup = new { inline_keyboard = new[] { new object[] {
    new { text = "✅ Allow", callback_data = PermissionAction.Id("allow", req.Id) },
    new { text = "✅ Always", callback_data = PermissionAction.Id("allowAlways", req.Id) },
    new { text = "⛔ Deny",  callback_data = PermissionAction.Id("deny", req.Id) } } } };
var text = $"🔒 The agent wants to use {req.Tool}." +
           (string.IsNullOrWhiteSpace(req.Summary) ? "" : $"\n> {Trim(req.Summary!, 600)}");
var mid = await _tg.SendMessageAsync(chatId, text, threadId, markup, ct);
if (mid is null) return false;
await _store.SetPromptMessageAsync(req.Id, "telegram", chatId, mid, ct);
return true;
```

`MarkExpiredAsync`: `EditMessageTextAsync(req.Channel!, req.MessageTs!, $"⏰ Expired — {req.Tool}. Please answer in the web terminal.", null, ct)`.

**Step 2:** `TelegramUpdateService` — long-poll loop (resilience pattern of `SlackSocketModeService.ExecuteAsync`: run-once + catch + 5 s delay; don't start when `!_opts.CanRun`). Per update string → `TelegramUpdate.Parse`; dispatch:

- **Callback** (`u.Kind == Callback`): `PermissionAction.TryParse(u.CallbackData, …)`; `ResolveAsync`; on success edit the message (`✅ Allowed — Tool (won't ask again this run) · by @user` / `⛔ Denied — …`, no markup) and `AnswerCallbackAsync(u.CallbackId, "Done", ct)`. On null resolve → `GetAsync(reqId)` and answer callback with `"Already decided"` / `"Expired — answer in the web terminal"` (toast only; the message was already rewritten by whoever decided it).
- **Command `/start <code>` or `/link <code>`** (parse from `u.Text`): `ConsumeAsync(code, "telegram")` → `SetTelegramLinkAsync(owner, u.ChatId, u.IsForumChat)`; reply "✅ Linked! Session updates will arrive here." or "⚠️ Invalid or expired code." (always in the same chat/thread the command came from).
- **Command `/sessions` or `!sessions`**: `ListByChatAsync("telegram", u.ChatId)` → for each, one line `#{tag} · {active-marker}` — enrich with `ISessionService.GetSessionAsync(binding.Owner, binding.SessionId)` for phase/title; reply.
- **Command `/use <tag>` or `!use <tag>`**: find unique binding of this chat whose session id matches the tag prefix (`ChatFormatting.MatchesTag`); ambiguous → "be more specific"; found → `SetActiveAsync` + confirm.
- **Command `/status` or `!status`**: resolve target session (thread binding → reply mapping → active) then answer with `ChatFormatting.StatusText` (same data sources as Task 8; inject `ISessionStore`, `PermissionStore`, `ISessionService`).
- **Plain message**: resolve session — (1) `u.ThreadId != null` → `GetByThreadAsync`; (2) `u.ReplyToMessageId != null` → `GetSessionByMessageAsync`; (3) → `GetActiveAsync`. No target → reply "No active session in this chat." Deliver like Slack does (`SlackSocketModeService.HandleEventAsync:110-118`): look up session, check `PodIp`/`Phase == "Running"` (else warn into the chat), `AgentTerminal.SendInputAsync`, then start the working indicator:

```csharp
var statusId = await _tg.SendMessageAsync(binding.ChatId, WorkingIndicator.Frames[0], binding.ThreadId, null, ct);
if (statusId is not null)
{
    await _bindings.SetStatusRefAsync("telegram", binding.SessionId, statusId, ct);
    _indicator.Start(binding.SessionId, (text, c) => _tg.EditMessageTextAsync(binding.ChatId, statusId, text, null, c));
}
```

Also: once per 24 h inside the loop call `_bindings.PruneMessagesAsync(ct)` (track `DateTime lastPrune`).

**Step 3:** Register in `Program.cs`:

```csharp
builder.Services.AddSingleton<AgentHub.Api.Chat.Telegram.TelegramPermissionNotifier>();
builder.Services.AddSingleton<AgentHub.Api.Permissions.IPermissionNotifier>(sp => sp.GetRequiredService<AgentHub.Api.Chat.Telegram.TelegramPermissionNotifier>());
builder.Services.AddSingleton<AgentHub.Api.Permissions.IPermissionPromptEditor>(sp => sp.GetRequiredService<AgentHub.Api.Chat.Telegram.TelegramPermissionNotifier>());
builder.Services.AddHostedService<AgentHub.Api.Chat.Telegram.TelegramUpdateService>();
```

⚠️ Ordering: `InternalController` still takes a single `IPermissionNotifier` at this point — the multi-notifier chain lands in Task 15. To keep every commit green, do Task 15's controller change **before** registering the second `IPermissionNotifier`, or fold that one-line controller change into this task (preferred: fold it in — see Task 15 step 1 for the exact code, and move Task 15's test with it).

**Step 4:** Build + tests PASS. Commit: `feat: telegram long-polling service, commands, permission buttons`

---

## Phase 4 — Signal

### Task 13: Options, client, envelope parser (TDD), verification flow

**Files:**
- Create: `backend/Chat/Signal/SignalOptions.cs` (`Chat:Signal`: `Enabled`, `ApiUrl`, `Number`, `bool CanRun => Enabled && ApiUrl != "" && Number != ""`)
- Create: `backend/Chat/Signal/SignalClient.cs`
- Create: `backend/Chat/Signal/SignalEnvelope.cs` (pure parser)
- Test: `tests/AgentHub.Api.Tests/SignalEnvelopeTests.cs`
- Modify: `backend/Controllers/ChatController.cs` (signal endpoints)
- Modify: `backend/Program.cs` (register; extend `/api/config` with `signalEnabled`)

**Step 1: Failing parser tests** — shapes from signal-cli-rest-api (json-rpc receive):

```csharp
public class SignalEnvelopeTests
{
    [Fact]
    public void ParsesTextWithQuote()
    {
        var e = SignalEnvelope.Parse("""
            {"envelope":{"sourceNumber":"+491700000001","timestamp":1752900000001,
             "dataMessage":{"message":"yes do it","timestamp":1752900000001,
               "quote":{"id":1752899000000,"author":"+491520000000"}}},"account":"+491520000000"}
            """);
        Assert.Equal("+491700000001", e!.Sender);
        Assert.Equal("yes do it", e.Text);
        Assert.Equal("1752899000000", e.QuotedTimestamp);
        Assert.Null(e.ReactionEmoji);
    }

    [Fact]
    public void ParsesReaction()
    {
        var e = SignalEnvelope.Parse("""
            {"envelope":{"sourceNumber":"+491700000001","timestamp":1752900000002,
             "dataMessage":{"reaction":{"emoji":"👍","targetSentTimestamp":1752899000000,"isRemove":false}}}}
            """);
        Assert.Equal("👍", e!.ReactionEmoji);
        Assert.Equal("1752899000000", e.ReactionTargetTimestamp);
        Assert.Null(e.Text);
    }

    [Fact]
    public void IgnoresReactionRemovalReceiptsAndJunk()
    {
        Assert.Null(SignalEnvelope.Parse("""{"envelope":{"sourceNumber":"+4917","dataMessage":{"reaction":{"emoji":"👍","targetSentTimestamp":1,"isRemove":true}}}}"""));
        Assert.Null(SignalEnvelope.Parse("""{"envelope":{"sourceNumber":"+4917","receiptMessage":{"isDelivery":true}}}"""));
        Assert.Null(SignalEnvelope.Parse("nope"));
    }
}
```

**Step 2:** FAIL. **Step 3:** Implement `SignalEnvelope` record (`Sender, Timestamp, Text?, QuotedTimestamp?, ReactionEmoji?, ReactionTargetTimestamp?`) + `Parse` (null unless there is a `dataMessage` with text or a non-remove reaction; prefer `sourceNumber`, fall back to `source`).

`SignalClient` (HttpClient against `_opts.ApiUrl`):
- `Task<string?> SendAsync(string recipient, string text, CancellationToken ct)` → POST `/v2/send` `{message, number: _opts.Number, recipients: [recipient]}` → returns `timestamp` from the response as string.
- `Task TryDeleteAsync(string recipient, string timestamp, CancellationToken ct)` — remote delete of an own message. ⚠️ Verify the endpoint against the deployed signal-cli-rest-api swagger (`{ApiUrl}/v1/docs`); current releases expose remote delete via json-rpc `sendRemoteDelete`/`remoteDelete` param on send — if the deployed version has no REST endpoint, implement as no-op with a debug log (the indicator then just stays as a normal message; acceptable degradation, do NOT block on this).
- WebSocket receive is consumed directly in `SignalReceiveService` (next task) via `ClientWebSocket` on `{ApiUrl→ws}/v1/receive/{number}`.

**Step 4: Verification flow** in `ChatController`:
- `PUT api/chat/signal` body `{enabled, number?}`: always `SetSignalEnabledAsync`; when `number` present and ≠ current: normalize (`+`digits), `SetSignalNumberAsync`, generate 6-digit code via `ChatLinkCodeStore.CreateAsync(owner, "signal-verify", number)`, `await _signal.SendAsync(number, $"Open AgentHub verification code: {code}", ct)`. Return 202.
- `POST api/chat/signal/verify` body `{code}`: `ConsumeAsync(code, "signal-verify")` → owner must match caller and payload must equal the stored number → `SetSignalVerifiedAsync(owner, true)`; 204 or 400.
- Extend `ChatMe`/`GET me` accordingly.

**Step 5:** Register options/client in Program.cs; `/api/config` gains `signalEnabled`. Build + tests PASS. Commit: `feat: signal client, envelope parser, number verification`

### Task 14: `SignalNotifier` + `SignalReceiveService` + permissions via reactions

**Files:**
- Create: `backend/Chat/Signal/SignalNotifier.cs`
- Create: `backend/Chat/Signal/SignalPermissionNotifier.cs`
- Create: `backend/Chat/Signal/SignalReceiveService.cs`
- Modify: `backend/Program.cs`

**Step 1:** `SignalNotifier : INotifier` — like `TelegramNotifier` but: target = `user.SignalNumber` only when `SignalEnabled && SignalVerified`; no topics (`ThreadId = null`); every outgoing send's returned timestamp → `RecordMessageAsync("signal", number, ts, sessionId)`. First-contact header ends with: `"Quote a message of this session to answer it; plain replies go to the newest session. !status shows progress."` Working indicator: stop → `TryDeleteAsync(chatId, statusRef)` + clear (no animation on Signal — static message, set `StatusRef` only).

**Step 2:** `SignalPermissionNotifier : IPermissionNotifier, IPermissionPromptEditor` (`Platform => "signal"`):

```
🔒 The agent wants to use {Tool}.
> {summary…}
React 👍 to allow, 👎 to deny — or quote this message and reply "always".
```

Store `SetPromptMessageAsync(req.Id, "signal", number, sentTimestamp)`. `MarkExpiredAsync`: send follow-up `"⏰ The permission request for {Tool} expired — please answer in the web terminal."` (can't edit).

**Step 3:** `SignalReceiveService` (BackgroundService, `_opts.CanRun` gate, reconnect loop): `ClientWebSocket` to `ws(s)://…/v1/receive/{number}`; each frame → `SignalEnvelope.Parse`; sender must resolve to a verified user — `GetBySignalNumberAsync(e.Sender)`, else ignore. Dispatch:

- **Reaction**: `GetAsync`-by-prompt? No — permission prompts are found via `PermissionStore`: add `GetByPromptMessageAsync(platform, channel, messageRef)` to `PermissionStore` (`SELECT … WHERE platform=@p AND channel=@c AND message_ts=@m`). 👍→allow, 👎→deny via `ResolveAsync`; confirm with a short send (`"✅ Allowed — {Tool}"` / `"⛔ Denied — {Tool}"`). Reactions on non-prompt messages are ignored.
- **Text `!sessions` / `!use <tag>` / `!status`**: same logic as Telegram (share it: put the command handling into a small helper `backend/Chat/ChatCommands.cs` — pure-ish, takes the store lookups as delegates or simply duplicate the ~20 lines per platform; prefer the helper only if it stays simple — judgement call, don't build a framework).
- **Text `always` with quote of a prompt**: quoted timestamp → `GetByPromptMessageAsync("signal", sender, quotedTs)` → resolve `allowAlways` + confirm.
- **Plain text**: route — quote → `GetSessionByMessageAsync("signal", sender, quotedTs)`; no quote → `GetActiveAsync("signal", sender)`; deliver via `AgentTerminal.SendInputAsync` (same running-check as Telegram, warnings sent back via Signal); then static working indicator: `SendAsync` frame 0 → `SetStatusRefAsync`.
- Daily `PruneMessagesAsync` here too? No — Telegram's service already does it; if Telegram is disabled and Signal enabled, prune here as well: guard with the same lastPrune pattern (cheap, do it).

**Step 4:** Register: `SignalNotifier` as `INotifier`; `SignalPermissionNotifier` singleton aliased to both interfaces (after Slack and Telegram registrations — chain order Slack → Telegram → Signal); `AddHostedService<SignalReceiveService>()`.

**Step 5:** Build + tests PASS. Commit: `feat: signal notifier, receive loop, reaction-based permissions`

---

## Phase 5 — Wiring, frontend, helm, docs

### Task 15: Permission notifier chain (TDD) — if not already folded into Task 12

**Files:**
- Modify: `backend/Controllers/InternalController.cs`
- Test: `tests/AgentHub.Api.Tests/PermissionChainTests.cs`

**Step 1:** Change `IPermissionNotifier _permNotifier` → `IEnumerable<IPermissionNotifier> _permNotifiers`; in `RequestPermission` replace the single `PostAsync` with the chain, extracted as a testable static:

```csharp
// backend/Permissions/PermissionRelay.cs
namespace AgentHub.Api.Permissions;

public static class PermissionRelay
{
    /// <summary>First notifier that posts wins (user's preferred platforms in registration order).</summary>
    public static async Task<bool> TryPostAsync(IEnumerable<IPermissionNotifier> notifiers, PermissionRequest req, CancellationToken ct)
    {
        foreach (var n in notifiers)
            if (await n.PostAsync(req, ct)) return true;
        return false;
    }
}
```

**Step 2: Test** (`PermissionChainTests.cs`) with two fake notifiers (records implementing the interface): first-false-second-true → true and second called; first-true → second **not** called; all-false → false.

**Step 3:** Full suite PASS. Commit: `feat: ordered permission notifier chain (slack → telegram → signal)`

### Task 16: Frontend — chat settings sections

**Files:**
- Modify: `frontend/src/api.js` (add: `chatMe()`, `telegramLinkCode()`, `setTelegramPrefs(p)`, `unlinkTelegram()`, `setSignalPrefs(p)`, `verifySignal(code)` — follow the existing `slackMe`/`setSlackPrefs` fetch pattern)
- Modify: `frontend/src/components/SettingsDialog.vue`
- Modify: `frontend/src/auth.js` or wherever `config.slackEnabled` is populated — add `telegramEnabled`, `signalEnabled` from `/api/config`
- Test: `frontend/src/components/settings-chat.test.js`

**Step 1: Failing vitest** — mount SettingsDialog with mocked api/config (mirror the setup style of `views.test.js` / existing component tests): telegram enabled → section visible, "Generate link code" button calls `api.telegramLinkCode` and displays code + deep link; signal enabled → number input, save calls `setSignalPrefs`, verify field calls `verifySignal`. Platforms disabled → sections absent.

**Step 2:** Implement sections (below the Slack section, same markup/classes):
- **Telegram**: status line (`linked ✓ (forum group)` / `not linked`), enable checkbox, "Generate link code" → shows `code` + link `https://t.me/<bot>?start=<code>` + hint "Open the link, or send /link <code> to the bot — in a DM or in your forum group." Unlink button when linked.
- **Signal**: hint that the instance must have Signal configured; number input (+E.164 placeholder), Save → "code sent via Signal", verify-code input + Verify button, status `verified ✓`; enable checkbox.

**Step 3:** `cd frontend && npm test` — PASS. Commit: `feat: telegram/signal settings sections`

### Task 17: Frontend — desktop notifications

**Files:**
- Create: `frontend/src/lib/desktop-notify.js`
- Test: `frontend/src/lib/desktop-notify.test.js`
- Modify: `frontend/src/App.vue`, `frontend/src/components/SettingsDialog.vue`

**Step 1: Failing test** for the pure transition detector:

```js
import { detectAlerts } from './desktop-notify.js'

const s = (id, over = {}) => ({ id, title: 't' + id, questionPending: false, status: 'Running', ...over })

test('question transition fires once', () => {
  const prev = [s(1)], next = [s(1, { questionPending: true })]
  expect(detectAlerts(prev, next)).toEqual([{ id: 1, title: 't1', kind: 'question' }])
  expect(detectAlerts(next, next)).toEqual([])   // no repeat while still pending
})

test('finish and fail transitions fire', () => {
  const prev = [s(1), s(2)]
  const next = [s(1, { status: 'Succeeded' }), s(2, { status: 'Failed' })]
  expect(detectAlerts(prev, next).map(a => a.kind)).toEqual(['finished', 'failed'])
})

test('new sessions never alert', () => {
  expect(detectAlerts([], [s(1, { questionPending: true })])).toEqual([])
})
```

**Step 2:** Implement `detectAlerts(prevSessions, nextSessions)` (map prev by id; only transitions of known ids) plus:

```js
export const desktopNotifyEnabled = () => localStorage.getItem('desktopNotify') === '1'
export async function setDesktopNotify(on) { /* store; if on && Notification.permission === 'default' → requestPermission() */ }
export function showAlert(alert) { /* if enabled && permission granted && document.hidden → new Notification(...) with body per kind; onclick → window.focus() + dispatch custom event 'open-session' with alert.id */ }
```

**Step 3:** `App.vue`: in `refresh()` keep the previous sessions array, call `detectAlerts(prev, sessions.value)` and `showAlert` each; listen for the `open-session` custom event → `activeId.value = id`. `SettingsDialog.vue` (notifications section): checkbox "Desktop notifications when a session waits" bound to the helper.

**Step 4:** `npm test` PASS. Commit: `feat: desktop notifications when a session waits or finishes`

### Task 18: Helm chart

**Files:**
- Modify: `helm/open-agenthub/values.yaml`, `templates/secret.yaml`, `templates/configmap.yaml`, `templates/networkpolicy.yaml` (inspect first)
- Create: `helm/open-agenthub/templates/signal-cli.yaml`

**Step 1: values.yaml** (top level, after `n8n:` — community, not under `ee:`):

```yaml
# Community chat integrations: session updates + replies via Telegram and/or Signal.
chat:
  telegram:
    enabled: false
    botToken: ""      # from @BotFather
  signal:
    enabled: false
    number: ""        # registered sender number, e.g. "+15551234567"
    image: bbernhard/signal-cli-rest-api:0.93
    storage: 1Gi      # PVC for the number registration/keys
```

**Step 2:** `secret.yaml`: `Chat__Telegram__BotToken` when set. `configmap.yaml` (inspect how `Ee__Slack__Enabled` is passed; same pattern): `Chat__Telegram__Enabled`, `Chat__Signal__Enabled`, `Chat__Signal__Number`, `Chat__Signal__ApiUrl: http://{{ include "agenthub.fullname" . }}-signal-cli:8080` (check the helper name in `_helpers.tpl` — reuse whatever prefix the other services use).

**Step 3:** `signal-cli.yaml` — `{{- if .Values.chat.signal.enabled }}`: Deployment (1 replica, image from values, env `MODE=json-rpc`, volumeMount `/home/.local/share/signal-cli` from PVC) + PVC (`chat.signal.storage`) + ClusterIP Service port 8080. Add a comment block: registration is one-time via `kubectl port-forward` + `curl http://localhost:8080/v1/register/<number>` or QR link-device, see README.

**Step 4:** `networkpolicy.yaml`: read it; if backend egress is restricted, allow backend → signal-cli:8080 (and confirm egress to api.telegram.org:443 is possible — most likely general internet egress is already open since Slack Socket Mode works).

**Step 5:** `helm template helm/open-agenthub --set chat.signal.enabled=true --set chat.telegram.enabled=true --set chat.telegram.botToken=x | grep -A5 signal-cli` renders without error.

**Step 6:** Commit: `feat(helm): telegram + signal-cli-rest-api deployment`

### Task 19: Docs + final verification

**Files:**
- Modify: `README.md` (feature list + a "Chat integrations" setup section: BotFather steps, forum-group hint with `manage_topics` bot permission, signal-cli registration one-liner, both community/free)
- Modify: `ee/README.md` only if it lists Slack as "the" chat integration — clarify EE = Slack, community = Telegram/Signal.

**Step 1:** Write docs (English, like the rest of the README; no company references).

**Step 2: Full verification**

```
dotnet build backend && dotnet test tests/AgentHub.Api.Tests
cd agent-runtime/session-agent && node --test test/
cd frontend && npm test && npm run build
helm template helm/open-agenthub --set chat.telegram.enabled=true --set chat.telegram.botToken=x --set chat.signal.enabled=true >/dev/null
```

Expected: everything green.

**Step 3:** Commit: `docs: telegram & signal chat integration setup`

---

## Manual end-to-end checklist (post-merge, real deployment)

1. Telegram DM: link via deep link → start session → question arrives → plain reply lands in session → indicator appears → disappears with next question.
2. Telegram forum group: `/link` in group → each session gets a topic → reply in topic routes correctly; permission buttons work; `!status` answers.
3. Two parallel sessions in a DM: reply-to routes to the right one; `/use` switches the plain-reply target.
4. Signal: verify number → question arrives → quote-reply routes; 👍 approves a permission; expiry rewrites/notifies after 30 min.
5. Slack regression: long answer arrives in multiple messages; expired permission shows "Expired".
