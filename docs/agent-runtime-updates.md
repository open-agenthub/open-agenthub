# Keeping the agent runtimes current

## Why the image is the update unit

A session cannot update its own agent CLI. `AgentPodSpecFactory` gives every non-root
session `ReadOnlyRootFilesystem = true` and runs it as uid 1000, while the CLIs live in
`/usr/local/lib/node_modules`. A self-update would have to write there, so it fails by
construction — not for want of a permission that could be granted, but because the
read-only root filesystem is the point.

Self-update prompts are unsafe in a managed PTY: the Workspace can hide the terminal picker,
then the Enter that submits a message confirms an update and ends the agent process. Disable
the CLI's updater rather than relying on the read-only filesystem to make installation fail.
The nightly image rollout remains the update mechanism; explicit user-run update commands
are outside this automatic-startup policy.

## Disabling in-session updates

Verified against the shipped packages on 2026-10-17:

| Agent | Inspected version | Runtime control |
|---|---|---|
| Codex | 0.160.0 | `check_for_update_on_startup = false` in read-only `/etc/codex/requirements.toml` |
| Claude Code | 2.1.287 | Entry point exports `DISABLE_AUTOUPDATER=1` |
| Cursor | 2026.10.16-e3cb207 (installer's current unpinned build) | Driver passes `--disable-auto-update` before the prompt in interactive, resumed and unattended commands |
| OpenClaw | 2026.7.1-2 | Entry point exports `OPENCLAW_NO_AUTO_UPDATE=1`; after restore, `update-policy.js` writes `update.checkOnStart=false` and `update.auto.enabled=false` |
| OpenCode | 1.18.35 | Existing `OPENCODE_DISABLE_AUTOUPDATE=1` and managed `autoupdate:false` retained |

**Codex:** the tagged [update checker](https://github.com/openai/codex/blob/rust-v0.160.0/codex-rs/tui/src/updates.rs)
returns before both discovery and popup presentation when the setting is false. Discovery
fetches the GitHub release, then checks that npm has matching ready metadata; the result goes
into `$CODEX_HOME/version.json`. A later launch reads that cache to show the picker. Removing
only the cache is therefore insufficient. Setting the key only in writable user config was
also rejected: the pinned [requirements schema](https://github.com/openai/codex/blob/rust-v0.160.0/codex-rs/config/src/config_requirements.rs)
supports this exact key, and requirements override user, project and command-line opt-ins.
No new CLI flag or popup-detection heuristic is needed.

The real-CLI proof is [codex-update-smoke.js](../agent-runtime/session-agent/fixtures/codex-update-smoke.js).
It uses an ephemeral CA and a loopback CONNECT proxy serving only fake release/npm metadata
for version `99.0.0`, a disposable home, and the real npm-installed binary. All other proxy
destinations fail. `proot` maps a scratch policy onto `/etc/codex/requirements.toml` without
altering the host's policy. Run with Node, openssl, proot, and a built node-pty module:

```bash
npm install --prefix /tmp/codex-update-cli @openai/codex@0.160.0
CODEX_BIN=/tmp/codex-update-cli/node_modules/.bin/codex \
PROOT_BIN=/absolute/path/to/proot \
node agent-runtime/session-agent/fixtures/codex-update-smoke.js
```

The first launch must fetch both fake endpoints and cache `99.0.0`; the second must show
`Update now (runs npm install -g @openai/codex)`. With the managed setting, the same cache
must produce no picker even with user config and `-c check_for_update_on_startup=true`.
With an empty cache, it must also make zero updater requests and reach the composer.
The fixture kills its owned process groups and removes its home and certificates.

**Claude Code:** the pinned native package's updater-disable resolver checks
`DISABLE_AUTOUPDATER` before the legacy `autoUpdates` preference. The entrypoint sets it
after common startup so restored preferences cannot enable background installation.
This does not prohibit an explicitly requested `claude update`.

**Cursor:** the downloaded bundle declares the hidden `--disable-auto-update` option and
passes it to the startup code that otherwise schedules a background update after two
seconds. A config key was rejected because the inspected code gates this on the parsed
option. The real CLI accepts the option and prints its version; driver tests cover option
ordering and the login wrapper. Since the installer is unpinned, each image smoke must
continue checking flag compatibility. This covers AgentHub's launched CLI, not a separate
`agent` command somebody starts manually from the shell.

**OpenClaw:** the shipped `dist/update-startup-*.js` has independent hint and auto-install
gates: `update.checkOnStart` and `update.auto.enabled` plus `OPENCLAW_NO_AUTO_UPDATE`.
Disabling just auto-install leaves startup hints enabled. AgentHub currently uses local
agent/TUI commands rather than a managed gateway, but it disables both gates for a gateway
started in the session too. The pinned CLI validates the generated config; the rewrite
preserves unrelated fields and replaces restored opt-ins on every pod start.

**OpenCode:** the pinned binary returns before fetching the latest version when either
`autoupdate === false` or `OPENCODE_DISABLE_AUTOUPDATE` is true, including its notify-only
path. The existing managed config is merged last, so a user config cannot opt in again.

The Codex fake-service proof exercises the actual updater. The other agents were checked
through their shipped code, real CLI option/version or config validation, and affected
runtime tests; their update services and authenticated background installs were not run.
The Workspace composer readiness guard belongs to #112's Workspace owner and is separate
from these runtime changes.

## How the pins work

Each runtime Dockerfile declares its versions as build args and verifies after install:

```dockerfile
ARG CLAUDE_CODE_VERSION=2.1.283
ENV CLAUDE_CODE_VERSION=${CLAUDE_CODE_VERSION}
RUN npm install -g @anthropic-ai/claude-code@${CLAUDE_CODE_VERSION} \
 && claude --version 2>&1 | grep -Fq "$CLAUDE_CODE_VERSION"
```

The `ENV` makes the shipped version visible from inside a running session, which is what
an update notification in the UI would compare against.

**Cursor is the exception — it has no pin.** `https://cursor.com/install` offers no
version selector and always installs the newest build, so the image cannot choose a
version. It used to assert an expected one, which turned "not reproducible" into "build
broken" on every Cursor release and blocked the entire image set twice. The image now
records what it got in `/usr/local/share/cursor-agent/INSTALLED_VERSION`, and
`cursor/entrypoint.sh` exports it as `CURSOR_AGENT_VERSION` so a running session can still
report its version. Cursor is current by construction; nothing needs bumping.

**OpenClaw is deliberately held back.** `sync-auth-profiles.js` reads the agent's
auth-profile store directly, and its layout is documented per version in the Dockerfile.
The nightly job reports a new OpenClaw release but does not rewrite the pin.

**OpenCode is held back for the same kind of reason.** Its approval gate is a plugin hooked into
`tool.execute.before`, and the credential watcher reads `auth.json` in the 1.18.x layout. A newer
CLI that still installs and prints its version proves neither. Before raising
`OPENCODE_VERSION`, check five things against the new release:

1. **The gate still fires and still blocks.** Run `opencode run` against a fake OpenAI-compatible
   endpoint that answers with one `bash` tool call, with the managed config pointing at
   `policy-plugin.mjs` and a fake callback answering `/agent-policy` with `deny`. The call
   must fail with "Blocked by the session policy." and the turn must still finish. In 1.18.x the
   `permission.ask` hook is declared but never called, so do not switch the plugin to it
   without first proving that it fires.
2. **MCP tool names are still `<server>_<tool>`.** The gate maps them to `mcp__server__tool`. If
   the separator changes, the MCP sharing policy stops matching.
3. **`auth.json` still maps provider ids to `{ type: "api" | "oauth" | "wellknown", … }`.**
   `ProviderCredentialValidator` and `opencode/auth-watcher.js` both pin that shape.
   The account identity (`ProviderAccountIdentityReader.FromOpenCode`) also reads the api `key`
   and the oauth `accountId` from it.
4. **`session.idle` still reaches the plugin, and the plugin client still lists messages.** The
   chat relay (`opencode/turn-notify.mjs`) depends on both and on `parentID` marking subagent
   sessions. In an interactive `opencode run` with a fake `/notify`, exactly one `question`
   must arrive with the final text, also when the model ran a `task` subagent.
5. **A Go limit still arrives as a retry status with its sentence.** Have the fake endpoint answer
   429 with `retry-after` and a body naming `GoUsageLimitError` (and once `FreeUsageLimitError`).
   `opencode/limit-notice.mjs` waits for `session.status` with `type: "retry"` and the reasons
   `account_rate_limit` / `free_tier_limit`, and the OpenCode `limitPatterns` in
   `opencode/driver.js` match the sentence: `… usage limit reached. It will reset in …` and
   `Free usage exceeded, subscribe to Go`. Read the new binary for both, as
   `docs/account-limits.md` describes; a reworded notice is a limit nobody detects.

The image also chooses the OpenCode binary itself rather than leaving it to the package's
`postinstall`. That script probes the *build* machine for AVX2. On a runner that has AVX2 it
installs a binary that dies with an illegal instruction on any node that lacks it. amd64 therefore
always gets the `baseline` build.

The nightly job runs the **smoke tests**, not just the build, because a newer CLI that
installs cleanly can still have dropped a guarantee AgentHub relies on — and only the
smoke tests would notice.

Two things about `fixtures/codex-policy-hook-smoke.js`, which pins the PreToolUse deny
contract by driving the real CLI against a fake model endpoint. Both bit during the
0.144.5 → 0.157.1 bump and will bite again:

- Codex renamed its shell tool from `shell_command` to `exec_command`, and its argument
  from `command` to `cmd`. The hook payload still normalises this to tool `Bash` with
  `input.command` — that normalisation is the contract, and it survived the rename.
- Codex now prefers a WebSocket transport for `/v1/responses`. The fixture's server only
  speaks HTTP, and the failed upgrade consumed the first canned response before the
  fallback engaged, so no tool call ever reached the hook. The fixture pins the endpoint
  to a provider with `supports_websockets = false`.

Both looked exactly like "the policy hook stopped firing". If this smoke test fails after
a bump, check the advertised tool names and the transport before concluding that
enforcement broke.

`fixtures/codex-turn-notify-smoke.js` pins the second managed-hook contract the same way:
the `Stop` hook's `last_assistant_message` and `transcript_path` fields, the hook process
inheriting `AGENTHUB_*`, and silence in exec mode. It is what opens a Codex session's chat
thread (`docs/chat-relay.md`), so a bump that renames either field turns every Codex
session invisible in Slack/Telegram/Signal without any other symptom.

### Custom model endpoints

Sessions on a self-hosted server (`docs/custom-model-endpoints.md`) depend on configuration
names each CLI may rename in a bump. Each one was checked against the pinned version, and none
of them is covered by a smoke test against a real server:

- **Claude Code** (2.1.287): `ANTHROPIC_BASE_URL`, `ANTHROPIC_AUTH_TOKEN`, `ANTHROPIC_MODEL`, the
  `ANTHROPIC_DEFAULT_*_MODEL` tiers, `API_TIMEOUT_MS` and `CLAUDE_STREAM_FIRST_BYTE_TIMEOUT_MS`.
- **Codex** (0.160.0): `[model_providers.<id>]` with `base_url`, `wire_api = "responses"`, `env_key`
  and `stream_idle_timeout_ms`. 0.160.0 refuses `wire_api = "chat"` at config load.
- **OpenCode** (1.18.35): a `provider` block with `npm: "@ai-sdk/openai-compatible"` (bundled with
  the CLI) and `options.baseURL` / `apiKey` / `headerTimeout`.

After a bump, check these names in the new version. A rename does not fail any unit test. It
shows up as a session that silently talks to the provider's default endpoint, or that waits on
the old 5-minute timeout.

### Slash command lists

Each runtime's `slash-commands.js` records the built-in commands of its pinned CLI for the
Workspace composer's completion (`docs/slash-commands.md`), read off the CLI's own "/" menu in a
PTY. After a bump, read the menu of the new version the same way and update the list and the
`cli` label. A stale list fails nothing: a removed command is still offered and the CLI answers it
as unknown, a new one is missing from the completion but still runs when typed.

## The nightly job

[`update-agent-runtimes.yml`](../.github/workflows/update-agent-runtimes.yml) runs at
04:00 UTC and on demand. It resolves the newest version of each CLI plus `gh` and `glab`,
rewrites the `ARG` lines, **builds and smoke-tests every changed runtime**, and
opens/updates a single PR on `chore/agent-runtime-versions`.

That verification step is the part that matters: it turns a stale pin into a failed nightly
job instead of a failed release build, and a silently dropped guarantee (the Codex hook
above) into a failed bump instead of a merged regression.

Because PRs opened with `GITHUB_TOKEN` do not trigger other workflows, that PR shows no
image-build check of its own — the job's own build is the evidence. Merging runs the real
build on `main`.

## Scanning

| What | Where | Blocking |
|---|---|---|
| Image CVEs (OS + language, HIGH/CRITICAL, fixed only) | Trivy in `build-images.yml` | no — reports to the Security tab |
| Code (C#, JS/TS), `security-and-quality` queries | `codeql.yml`, plus weekly | no |
| Dependency updates (NuGet, npm, Actions, base images) | `dependabot.yml`, weekly | n/a |

Trivy is non-blocking on purpose: base images routinely carry an unfixed CVE, and a gate
there stops unrelated deploys. Set `exit-code: 1` in the scan step to make it a gate.
