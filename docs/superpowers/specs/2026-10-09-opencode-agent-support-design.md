# OpenCode Agent Support Design

**Date:** 2026-10-09
**Status:** Implemented
**Scope:** OpenCode as the fifth session agent, built for the OpenCode Go subscription. Covers
backend, frontend, agent runtime, Helm, CI, and docs.

## Objective

Users with an OpenCode Go subscription ($10/month for curated open models) should be able to run
sessions on it. To them it should feel no different from Claude, Codex, Cursor, or OpenClaw: every
mode, both billing choices, resume, MCP, and skills.

## What OpenCode Go is, technically

Verified against `opencode-ai` 1.18.34/1.18.35 and its source:

- OpenCode Go is a provider inside the CLI, `opencode-go`. Its base URL is
  `https://opencode.ai/zen/go/v1` and its models are named `opencode-go/<model>`.
- It is billed by API key, with no OAuth. The key comes from the opencode.ai console. OpenCode reads
  it as `OPENCODE_API_KEY`, and the same variable also enables Zen pay-as-you-go.
- `opencode auth login` stores the key in `$XDG_DATA_HOME/opencode/auth.json`. The file maps
  provider ids to `{ type: "api", key }`; OAuth providers appear as `{ type: "oauth", … }`.

## Authentication

| Auth mode | Pod credential |
| --- | --- |
| ApiKey | `opencode_api_key` from the user's credentials, as `OPENCODE_API_KEY`, scoped to the agent child |
| Subscription | the user's `auth.json` (`opencode-{owner}` Secret), restored to the data directory |

Subscription mode is not limited to Go. `opencode auth login` can sign in to any provider OpenCode
supports, and the watcher stores whichever `auth.json` results. ApiKey mode is the direct route for a
Go subscriber, who only has to paste one key into Credentials.

**Alternative considered: ApiKey only.** Rejected because Subscription mode costs very little on
top. The watcher, Secret, and validator follow the Cursor pattern. It also covers users who sign in
to other providers through OpenCode.

## Default model

With only `OPENCODE_API_KEY` set, OpenCode picks its own default, and that is not necessarily a Go
model. A Go subscriber would then be billed against their Zen balance, or would fail. When the key
or the stored login is for `opencode-go`, `user-config.js` therefore sets `model`. It picks the first
model from an ordered preference list that `opencode models opencode-go` actually offers, starting
with `glm-5.3`. An ordered list rather than one pinned id, because the Go catalogue changes every few
weeks. `AGENTHUB_OPENCODE_MODEL` overrides the choice. A login for any other provider leaves the
choice to OpenCode. OpenClaw learned the same lesson in 0.12.0.

## Approvals: a plugin in the managed config

OpenCode has no command-hook system like Claude's or Codex's, but its JS plugins can hook
`tool.execute.before`:

- **When it fires.** The hook runs before OpenCode evaluates its own permission rules, for every
  tool call, MCP tools included.
- **How it blocks.** Throwing from the hook fails the call with that message. The model sees the
  failure and carries on. This was verified against the real CLI with a mock model.
- **What it rules out.** `permission.ask` is declared in the plugin types but is never called in
  1.18.x, so a gate built on it would never run.

`policy-gate.mjs` follows the Codex hook:

1. It asks `/agent-policy`.
2. When that answers "ask", it creates a `/permission` request and polls it.
3. It rechecks the policy after an approval, so a sharing-policy deny that arrived in the meantime
   still wins.

Unanswered requests expire: after 29 minutes for interactive sessions and 4 minutes for unattended
ones. Every path without an answer ends in a denial. Auto-approve is read off the session record on
every call, so OpenCode joins Claude and Codex in honouring a toggle without a restart.

**Where the plugin is registered matters more than what it does.** OpenCode merges the managed
config `/etc/opencode/opencode.json` last, so it outranks anything the agent can write. The pod
mounts that directory read-only from an emptyDir, and an init container writes it. This is the same
shape as `/etc/codex`, and it works for custom images. The managed config also sets
`permission: "allow"`; otherwise a call the hub had approved would be asked a second time, and
`opencode run` answers that second question by rejecting.

**Alternative considered: `--auto` / per-tool `permission` rules.** Rejected for two reasons:

- These rules are fixed at start, like Cursor's `--force`, so auto-approve could not be toggled.
- No approval request would ever reach the web UI or the chat relay.

## Tool names

Built-in tools are mapped to the names the hub's policy already uses: `bash` → `Bash`
(matched by command prefix), `read` → `Read`, and `edit`/`multiedit`/`patch`/`apply_patch` → `Edit`.
MCP tools arrive as `<server>_<tool>` and become `mcp__<server>__<tool>`, matching server names
longest first. One allow list therefore means the same thing for every agent.

`todowrite`, `todoread`, and `question` are not gated. They have no effect outside the conversation,
and putting an approver in front of them would only train people to click "allow" without reading.

## State and resume

Everything worth resuming lives in OpenCode's XDG data directory: the SQLite session database, undo
snapshots, and `auth.json`. The state archive takes one directory under `$HOME`, so the entrypoint
links `~/.local/share/opencode` to `~/.opencode`.

**Alternative considered: `XDG_DATA_HOME`.** Rejected because that variable would move every other
tool's data as well.

The archive excludes `auth.json` (as `authFilename`), `mcp-auth.json`, logs, and downloaded binaries.

Resume uses `--continue`, because OpenCode generates its own session ids and cannot be handed one in
advance. It picks the newest top-level session of the project, and when there is none it starts
fresh and exits 0. That means there is no missing-resume failure to fall back from.

## Image

The image installs `opencode-ai` pinned and verifies the version. On amd64 it always installs the
`baseline` binary, because the package's postinstall picks AVX2 or baseline by probing the build
machine. It then deletes the per-platform packages (~180 MB each), because the custom-image init
container copies the whole global `node_modules` into every pod.

## Non-goals

- Exposing OpenCode's model picker in the AgentHub UI. `/models` inside the TUI covers it.
- Chat UI mode for OpenCode (terminal only, like Codex, Cursor, and OpenClaw).
- Telemetry or cost import from `opencode stats`.
