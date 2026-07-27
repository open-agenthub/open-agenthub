# Clean Transcripts Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Return readable plain-text session transcripts without exposing terminal escape or control sequences.

**Architecture:** Preserve raw scrollback in S3 and Postgres. Extend the existing backend sanitizer to understand the provider TUI control sequences, then apply it after `KubernetesSessionService` selects the S3 transcript or Postgres fallback.

**Tech Stack:** C# 13, .NET 10, xUnit, `System.Text.RegularExpressions`

## Global Constraints

- Keep stored scrollback unchanged.
- Sanitize at the backend transcript boundary for owner and shared-session consumers.
- Do not add dependencies or network calls.
- Preserve ordinary text, spacing, and line feeds.
- Do not emulate a terminal screen or reconstruct overwritten rows.
- Do not include forbidden company names or internal environment values in versioned files.

---

### Task 1: Terminal control-sequence sanitizer

**Files:**
- Modify: `tests/AgentHub.Api.Tests/SlackAnsiTests.cs`
- Modify: `backend/Services/AgentTerminal.cs`

**Interfaces:**
- Consumes: `AgentTerminal.StripAnsi(string s)`
- Produces: `AgentTerminal.StripAnsi(string s) -> string` with CSI, OSC, short ESC, C1-control, and carriage-return cleanup

- [ ] **Step 1: Write the failing regression test**

Add an xUnit test whose input includes the reported Claude trust screen controls:

```csharp
[Fact]
public void StripsClaudeTrustScreenTerminalControls()
{
    var esc = ((char)27).ToString();
    var input = $"{esc}7{esc}[r{esc}8{esc}[?25h{esc}[?25l{esc}[?2004h{esc}[?1004h{esc}[?2031h"
        + $"{esc}[38;5;220m────{esc}[39m\n"
        + $"{esc}[2G{esc}[1mAccessing workspace:{esc}[22m{esc}[39m";

    Assert.Equal("────\nAccessing workspace:", AgentTerminal.StripAnsi(input));
}
```

The production mutation this catches is leaving short `ESC 7` / `ESC 8` or CSI sequences in transcript output.

- [ ] **Step 2: Run the focused test and verify RED**

Run:

```powershell
dotnet test tests/AgentHub.Api.Tests/AgentHub.Api.Tests.csproj --filter FullyQualifiedName~SlackAnsiTests.StripsClaudeTrustScreenTerminalControls
```

Expected: FAIL because `ESC 7` and `ESC 8` remain in the returned string.

- [ ] **Step 3: Implement the minimal sanitizer extension**

Update `AgentTerminal` so `StripAnsi` removes:

```csharp
private static readonly Regex C1 = new("[\u0080-\u009f]", RegexOptions.Compiled);
private static readonly Regex OtherEsc = new(Esc + "(?:[78]|[ -/]*[@-~])", RegexOptions.Compiled);
```

Run OSC, CSI, `OtherEsc`, then C1 replacement before the existing carriage-return normalization. Keep the more specific OSC and CSI expressions before the generic ESC expression.

- [ ] **Step 4: Run all sanitizer tests and verify GREEN**

Run:

```powershell
dotnet test tests/AgentHub.Api.Tests/AgentHub.Api.Tests.csproj --filter FullyQualifiedName~SlackAnsiTests
```

Expected: all `SlackAnsiTests` pass.

- [ ] **Step 5: Commit the sanitizer**

```powershell
git add backend/Services/AgentTerminal.cs tests/AgentHub.Api.Tests/SlackAnsiTests.cs
git commit -m "fix: strip terminal controls from transcript text"
```

### Task 2: Transcript retrieval boundary

**Files:**
- Modify: `tests/AgentHub.Api.Tests/SlackAnsiTests.cs`
- Modify: `backend/Services/AgentTerminal.cs`
- Modify: `backend/Services/KubernetesSessionService.cs`

**Interfaces:**
- Consumes: `AgentTerminal.CleanTranscript(string? s)`
- Produces: `AgentTerminal.CleanTranscript(string? s) -> string?`, preserving `null` while sanitizing non-null transcript text

- [ ] **Step 1: Write the failing null-aware transcript test**

Add:

```csharp
[Fact]
public void CleanTranscript_PreservesMissingTranscriptAndSanitizesStoredText()
{
    var esc = ((char)27).ToString();

    Assert.Null(AgentTerminal.CleanTranscript(null));
    Assert.Equal("stored text", AgentTerminal.CleanTranscript($"{esc}[31mstored text{esc}[0m"));
}
```

The production mutations this catches are returning an empty transcript for `null` or returning selected stored text without sanitizing it.

- [ ] **Step 2: Run the focused test and verify RED**

Run:

```powershell
dotnet test tests/AgentHub.Api.Tests/AgentHub.Api.Tests.csproj --filter FullyQualifiedName~SlackAnsiTests.CleanTranscript
```

Expected: build FAIL because `AgentTerminal.CleanTranscript` does not exist.

- [ ] **Step 3: Add the null-aware boundary and wire retrieval**

Add:

```csharp
public static string? CleanTranscript(string? s) => s is null ? null : StripAnsi(s);
```

Change `KubernetesSessionService.GetTranscriptAsync` to select the raw S3/Postgres text exactly as today and return:

```csharp
return AgentTerminal.CleanTranscript(raw);
```

This single post-selection call sanitizes either source while retaining S3 preference and the Postgres fallback.

- [ ] **Step 4: Run focused and complete backend tests**

Run:

```powershell
dotnet test tests/AgentHub.Api.Tests/AgentHub.Api.Tests.csproj --filter FullyQualifiedName~SlackAnsiTests
dotnet test tests/AgentHub.Api.Tests/AgentHub.Api.Tests.csproj
```

Expected: focused tests pass; complete suite passes with the same pre-existing skipped integration tests and dependency advisory.

- [ ] **Step 5: Commit transcript retrieval**

```powershell
git add backend/Services/AgentTerminal.cs backend/Services/KubernetesSessionService.cs tests/AgentHub.Api.Tests/SlackAnsiTests.cs
git commit -m "fix: sanitize stored session transcripts"
```

### Task 3: Final verification

**Files:**
- Verify only: all files committed by Tasks 1 and 2

**Interfaces:**
- Consumes: committed `feat/clean-transcripts` branch
- Produces: clean worktree and evidence that raw storage remains unchanged while returned transcript text is sanitized

- [ ] **Step 1: Inspect the scoped diff**

Run:

```powershell
git diff main...HEAD --check
git diff main...HEAD -- backend/Services/AgentTerminal.cs backend/Services/KubernetesSessionService.cs tests/AgentHub.Api.Tests/SlackAnsiTests.cs
```

Expected: no whitespace errors; only sanitizer and transcript-return behavior changes.

- [ ] **Step 2: Run the complete backend suite once more**

Run:

```powershell
dotnet test tests/AgentHub.Api.Tests/AgentHub.Api.Tests.csproj --no-restore
```

Expected: all runnable tests pass.

- [ ] **Step 3: Confirm branch state**

Run:

```powershell
git status --short --branch
git log --oneline main..HEAD
```

Expected: branch `feat/clean-transcripts`, clean worktree, and scoped design/implementation commits.
