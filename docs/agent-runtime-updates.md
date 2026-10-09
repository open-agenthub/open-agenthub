# Keeping the agent runtimes current

## Why the image is the update unit

A session cannot update its own agent CLI. `AgentPodSpecFactory` gives every non-root
session `ReadOnlyRootFilesystem = true` and runs it as uid 1000, while the CLIs live in
`/usr/local/lib/node_modules`. A self-update would have to write there, so it fails by
construction — not for want of a permission that could be granted, but because the
read-only root filesystem is the point.

An in-session "update available, restart?" prompt is therefore only ever a *notification*;
the install still happens by rolling out a new image. That keeps the deployment
reproducible: `image.tag` pins every runtime, and a given tag always contains a known set
of CLI versions.

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
`OPENCODE_VERSION`, check three things against the new release:

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
