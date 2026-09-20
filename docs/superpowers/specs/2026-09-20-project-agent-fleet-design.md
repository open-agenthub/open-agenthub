# Project Agent Fleet Design

**Date:** 2026-09-20  
**Status:** Approved design  
**Scope:** Open AgentHub Community Edition — session persistence, internal/remote APIs, in-pod and external MCP servers, frontend session views

## Objective

Let the sessions ("agents") of one project act as a fleet: discover each other,
send each other messages/tasks, and describe what they are for. Example fleet:
one agent reviews code, one watches issues, one implements and receives tasks
from the others.

Builds on the MCP session orchestration design (2026-07-27): the in-pod
`agenthub_sessions` MCP keeps its callback-token auth; the descendant rule for
the existing lifecycle tools (`session_get`/`session_wait`/`session_delete`)
is **not** relaxed. Peer access exists only through the new directory and
messaging tools.

## Product behavior

### Agent identity

- Agent **name** = session title (unchanged).
- New optional session **description** ("what is this agent for") — shown in the
  UI (create/edit forms, session lists) and in the agent directory so agents can
  pick the right peer. Max 500 characters.

### New MCP tools (in-pod `agenthub_sessions`)

| Tool | Behavior |
| --- | --- |
| `agents_list` | Directory of the session's project: all sessions of the same owner in the same project (id, title = name, description, phase, mode, agent, `self` marker). A session without a project sees only itself and its descendants (previous scope). |
| `agent_send` | `{ to, message }` — send a message/task to a peer. `to` is a session id or a unique title; title resolution uses the project directory, an ambiguous title fails with the candidate list. Max 4 000 characters. |
| `agent_inbox` | `{ waitSeconds? }` — fetch undelivered messages (long-poll up to 60 s per call, default immediate). Fetching marks them delivered; at most 4 per call (call again for more). Replies go back as normal `agent_send`. |

An autonomous fleet agent ends its prompt with "wait for new tasks via
`agent_inbox`" and loops; v1 is inbox pull — no prompt injection into a running
CLI.

### External MCP (`mcp/agenthub`, `oah_…` token)

- `agents_list { projectId? }` — owner sessions, optionally filtered by project.
- `agent_send { to, message }` — send to any owned session (from the user, not
  from a session; shown as "external"). Uses `POST /api/remote/sessions/{id}/messages`.
- `agent_inbox` is **not** offered externally — an API token is not a session
  and has no inbox (documented open point; a per-token inbox could follow).

## Architecture

### Persistence

- `sessions`: new nullable `description TEXT` column (idempotent `ALTER TABLE`
  migration in `PostgresSessionStore.InitializeAsync`, same pattern as the
  other columns). Round-trips through `SessionRecord`,
  `CreateSessionRequest`/`UpdateSessionRequest` (null = unchanged, empty
  clears), `SessionInfo`, duplication.
- New table `session_messages` (own store `PostgresSessionMessageStore`):
  `id, project_id, from_session_id (NULL = external sender), to_session_id,
  owner, body, created_at, delivered_at (NULL = undelivered)`; indexed on
  `(to_session_id, delivered_at)`. Registered in the GDPR account purge
  (owner column) and initialized at startup like the other stores.

### Backend APIs

**Internal (header `X-Agent-Token`, session id in path):**

- `GET /internal/sessions/{id}/project-agents` — slim directory records
  (`id, title, description, phase, mode, agent, questionPending, createdAt,
  self`), **not** full `SessionInfo`, so peers' MCP configs and runtime
  settings never reach a sibling pod. Scope: same owner + same non-null
  `ProjectId`; without a project: self + descendants only.
- `POST /internal/sessions/{id}/messages` — body `{ to, body }`; `to` must be a
  session of the same owner in the same (non-null) project, otherwise `404`
  (no existence leak). Self-send and empty/oversized bodies → `400`.
  Best-effort fan-out through the existing `INotifier`s (event
  `agent-message`) so Slack/Telegram/n8n see it like question/finished events.
- `GET /internal/sessions/{id}/messages?wait=<seconds>` — takes and marks
  undelivered messages for this session (batch ≤ 4, keeps responses under the
  MCP client's 64 KB cap); long-polls up to 60 s (1 s DB poll), returns
  `{ messages: [{ id, from, fromTitle, body, createdAt }] }` (`from` null =
  external sender).

**Public (user auth):**

- `GET /api/sessions/{id}/messages` — recent messages for the owner's session
  (read-only, does **not** mark delivered; the delivered flag belongs to the
  agent's inbox). Feeds the message banner in the session view.

**Remote (Bearer `oah_…`):**

- `POST /api/remote/sessions/{id}/messages` — body `{ body }`; inserts with
  `from_session_id = NULL`.

### Frontend

- Description input in New/Edit session (below the title, existing form
  pattern, no popups); description line in the sidebar session list and the
  sessions page rows; description included in free-text session search.
- Session view (TerminalView): undelivered agent messages appear as a banner
  (same pattern as permission prompts), polled alongside permissions;
  dismissible client-side.

## Security

- Peer visibility/messaging strictly same owner **and** same project; anything
  else answers `404`. Lifecycle tools keep the descendant rule.
- Message body limit 4 000 chars server-side; directory/messages responses are
  slim projections without secrets (no callback tokens, no MCP configs).
- MCP error results stay stable codes; title-resolution errors include only
  same-project candidate ids/titles (all same owner).

## Testing

- **xUnit:** message store CRUD/take-marks-delivered (PostgreSqlFact),
  description column round-trip, internal endpoints (auth, project scoping,
  cross-owner/-project 404, size limit, long-poll return/timeout), remote send,
  duplication copies the description.
- **node --test:** tool registration, client request shapes, title resolution
  (exact/ambiguous/missing), sanitize includes `description`.
- **vitest:** description in create/edit payloads and list rendering; message
  banner shows undelivered messages and dismisses.

## Non-goals (v1)

- Prompt injection into running CLIs (inbox pull only).
- Message threading, read receipts beyond `delivered_at`, retention policies.
- External (`oah_…`) inbox.
- Cross-owner fleets (EE sharing does not extend to agent messaging yet).
