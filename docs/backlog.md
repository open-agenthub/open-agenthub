# Backlog

Open work, with enough context that whoever picks an item up does not have to reconstruct why it
matters. Newest first within each section. Delete an item when it ships; move the reasoning into
[`development-log.md`](development-log.md) if it taught us something.

## In flight

### Files shared across a project's sessions
Today a file belongs to one session: `SessionFileKey` is
`sessions/{owner}/{sessionId}/files/{fileId}/{name}` and every lookup is session-scoped. Sharing
across the sessions of a project needs a project-scoped key or an explicit link, plus an access
rule — a session must not read another session's files just because both exist.

Worth designing alongside the item above, since both change what `materialize` returns.

### Sessions created by an API, taken over by a human later
Requested 2026-09-30. An interactive session currently ignores its prompt:
`agent-runtime/claude/driver.js` passes `AGENTHUB_PROMPT` only in the non-interactive branch, so a
session created with a task sits idle until somebody types. Needed:
- a prompt (and a system prompt) on an interactive session, passed on the command line
- `session_create` returns the **session URL**, so the creator can hand it to a person
- a poll endpoint for the session transcript, so the creator can follow progress without a
  websocket

## Waiting on the owner

- **Functional acceptance of the file path** — upload, preview, and an agent read through the UI.
  Six bugs in this path were each "fixed" before being confirmed; the last one was found by a user
  report, not by the suite. What to check now that `read_file` returns a path: ask a session to copy
  an uploaded PDF into its working directory and grep it, and confirm it reports a `localPath` under
  `/workspace/.agenthub/files` rather than only a name. Worth checking with object storage both
  reachable and unreachable from the pod — the two cases diverged in the backend until the agent
  content route stopped redirecting.
- **`v0.10.0`** — 54+ commits since `v0.9.0`, CI green. Cut it once acceptance passes.
- **Node 22 → 26** — evaluated, recommendation is HOLD. Note
  `.github/workflows/test.yml` still pins `node-version: 22` in two places; the Dependabot PRs do
  not touch it, so a bump there would leave CI testing the old version.
- **Marketing launch** — plan and drafts only. Nothing may be published before an explicit
  go-ahead.

## Known limits, accepted for now

- **Presigned URLs live 10 minutes** (`files.presignMinutes`). Fine for an immediate fetch; a
  transfer slower than that fails. Agent reads no longer depend on it.
- **Office previews need the renderer pod** (`files.officePreview.enabled`, off by default). The UI
  now says so and links to the README instead of implying the format is unsupported.
- **Plain SQL in the existing stores.** Parameterised, so not injectable, but new persistence code
  should use EF Core — `McpOAuthDbContext` already pulls it in. Do not rewrite working stores
  unprompted.
- **Scanner backlog untriaged** — roughly 200 Trivy and 190 CodeQL first-run alerts. Both scanners
  are non-blocking by design.
