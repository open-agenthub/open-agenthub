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

## The nightly job

[`update-agent-runtimes.yml`](../.github/workflows/update-agent-runtimes.yml) runs at
04:00 UTC and on demand. It resolves the newest version of each CLI plus `gh` and `glab`,
rewrites the `ARG` lines, **builds every changed runtime to prove the new pins install**,
and opens/updates a single PR on `chore/agent-runtime-versions`.

The build step is the part that matters: it is what turns a stale Cursor pin into a failed
nightly job instead of a failed release build.

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
