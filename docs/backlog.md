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
  Eight bugs in this path were each "fixed" before being confirmed; the last three were found by
  user reports, not by the suite. What to check now:
  - `read_file` on an uploaded PDF reports a `localPath` under `/workspace/.agenthub/files` rather
    than only a name, and the session can copy that file into its working directory and grep it.
    Worth checking with object storage both reachable and unreachable from the pod — the two cases
    diverged in the backend until the agent content route stopped redirecting.
  - a PDF renders in the preview pane instead of Chrome's blocked-content placeholder.
  - an upload survives closing the Files pane and switching sessions.
- **A session file has no delete affordance.** The listing in the Files pane offers no way to
  remove a file; the only delete the UI ever issued was the upload queue's teardown, which is the
  bug fixed on 2026-10-01. If files are meant to be deletable, that belongs on a listed row.
  Related: `SessionFileService.DeleteAsync` accepts any state, so "cancel my half-finished
  reservation" and "destroy a completed file" are the same request — worth splitting if the
  affordance arrives.
- **The 2026-08-01 files plan and design record no longer describe the code.** Both say the preview
  frame carries a `sandbox` attribute; it cannot, and the blob's MIME label replaced it. The design
  record also credits an "application content-security policy" that was never built — the only CSP
  header in the repo is the per-file one in `SessionFilesController`. Dated records, so left for a
  decision on whether to annotate them.
- **`v0.10.0`** — 54+ commits since `v0.9.0`, CI green. Cut it once acceptance passes.
- **Node 22 → 26** — evaluated, recommendation is HOLD. Note
  `.github/workflows/test.yml` still pins `node-version: 22` in two places; the Dependabot PRs do
  not touch it, so a bump there would leave CI testing the old version.
- **Marketing launch** — plan and drafts only. Nothing may be published before an explicit
  go-ahead.

## Known limits, accepted for now

- **Presigned URLs live 10 minutes** (`files.presignMinutes`). Fine for an immediate fetch; a
  transfer slower than that fails. `read_file` no longer depends on it — but **message attachments
  still do**: `AttachmentMaterializer` (`agent-runtime/files/materialize.js:100`) fetches the
  presigned `downloadUrl` rather than the agent content route. The window is short, since the url
  is minted in the same response, so this is about reachability rather than expiry: the fetch needs
  egress from the session pod to object storage, and the chart's `agent.extraEgressPorts` defaults
  to `[]` while the agent policy opens only 53/80/443/22. Object storage on a non-standard port
  therefore breaks attachments on a default install until that port is added. Moving it to the
  content route would remove the dependency entirely; it needs verifying with egress actually
  blocked, which is not something the suite can show.
- **Office previews need the renderer pod** (`files.officePreview.enabled`, off by default). The UI
  now says so and links to the README instead of implying the format is unsupported.
- **Plain SQL in the existing stores.** Parameterised, so not injectable, but new persistence code
  should use EF Core — `McpOAuthDbContext` already pulls it in. Do not rewrite working stores
  unprompted.
- **Scanner backlog untriaged** — roughly 200 Trivy and 190 CodeQL first-run alerts. Both scanners
  are non-blocking by design.
