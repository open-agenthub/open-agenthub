# MCP Session Orchestration Design

**Date:** 2026-07-27  
**Status:** Approved design  
**Scope:** Open AgentHub Community Edition — backend remote/internal APIs, session persistence, agent-runtime MCP, external MCP package, settings docs; no EE-only dependency

## Objective

Enable AI clients to start, inspect, wait on, and delete Open AgentHub sessions via MCP, for **team orchestration**: one controlling agent spawns specialized child sessions, tracks their phase, and cleans up.

Two callers share one create path through `ISessionService`:

1. **External AI** (Cursor Desktop, Claude Desktop, …) via a standalone MCP package authenticated with a personal API token (`oah_…`).
2. **In-session agent** via a runtime-owned stdio MCP (`agenthub_sessions`) authenticated with the existing per-session callback token (`X-Agent-Token`). Personal API tokens are **never** mounted into agent pods.

## Product behavior

### Tools (same names externally and in-pod)

| Tool | Behavior |
| --- | --- |
| `session_create` | Create and start a session. Default mode `Autonomous`; `prompt` required for Autonomous/Scheduled. |
| `session_get` | Return session info (id, title, mode, agent, authMode, phase, parentSessionId, …). |
| `session_list` | List owner sessions; optional filters `parentSessionId`, `phase`. |
| `session_wait` | Poll `session_get` until phase is `Succeeded` or `Failed`, or timeout. |
| `session_delete` | Delete a session (pod + record), same semantics as UI delete. |

### Create fields

Core: `title`, `prompt`, `mode` (default Autonomous), `agent`, `authMode`, `repos[]`, optional `projectId`, `policy`, `mcpConfigJson`, `parentSessionId`.

In-pod default: `parentSessionId = AGENTHUB_SESSION_ID` when omitted.

Created sessions are normal owner sessions and appear in the UI. Nesting depth is unlimited; soft limit is max **running** sessions per owner (`Pending|Running|Paused|Scheduled`). Exceeding the limit returns `429`.

### Delete semantics

- **External (`oah_…`):** delete any session owned by the token owner.
- **In-pod (callback):** delete only if target owner equals parent owner **and** target is a descendant of the current session (direct child or deeper). Sibling / unrelated root sessions → `404` (no existence leak).
- **No cascade:** deleting a parent does not delete children; children remain ordinary sessions (`parent_session_id` may point at a missing parent).

### Non-goals (v1)

- Pause / resume via MCP
- Structured result callback from child to parent (beyond phase polling)
- Hard nesting depth cap
- Parent badge / orchestration UI (API field only in v1)
- Live Kubernetes E2E in CI

## Architecture

```
Externe KI ──stdio MCP (mcp/agenthub)──► /api/remote/* ──┐
                                                         ├─► ISessionService ──► K8s Pods
Agent-Pod ──stdio MCP (agenthub_sessions)──► /internal/ ─┘
              (X-Agent-Token = parent session only)
```

### External package

- Repo path: `mcp/agenthub/` (Node stdio MCP).
- Config: `AGENTHUB_URL`, `AGENTHUB_TOKEN` (`oah_…`).
- Calls existing remote session API; extend remote surface for delete + soft-limit errors.
- Settings UI: document MCP snippet next to existing remote curl examples.

### In-pod MCP

- Path: `agent-runtime/.../sessions/server.mjs` (or `agent-runtime/session-agent/sessions/`), stdio MCP.
- Uses `AGENTHUB_CALLBACK_URL`, `AGENTHUB_SESSION_ID`, and the existing callback token env already injected for internal callbacks.
- Auto-injected into Claude / Codex / Cursor MCP config when feature flag enabled (same pattern as other runtime-owned MCP servers).
- `session_wait`: ~2s poll interval, default timeout 30 minutes; returns `{ timedOut, phase, id }` on timeout.

### Backend APIs

**Remote (Bearer `oah_…`):**

- Existing: `POST/GET /api/remote/sessions`, `GET /api/remote/sessions/{id}`
- Add: `DELETE /api/remote/sessions/{id}`
- Soft-limit enforced on create

**Internal (header `X-Agent-Token`, parent id in path):**

- `POST /internal/sessions/{parentId}/spawn` — body ≈ `CreateSessionRequest` + optional `parentSessionId` override (must remain under this parent or default to parent)
- `GET /internal/sessions/{parentId}/children` — list descendants / direct children
- `GET /internal/sessions/{parentId}/peer/{childId}` — get if descendant
- `DELETE /internal/sessions/{parentId}/peer/{childId}` — delete if descendant
- Auth: token must resolve to `parentId`; spawned child owner = parent owner

### Persistence

- Add nullable `parent_session_id` on `sessions` (indexed for list-by-parent).
- Expose `parentSessionId` on `SessionInfo`.
- No FK cascade delete.

### Configuration

- `AgentHub:SpawnMcpEnabled` (bool, default true or false per ops preference — default **true** in CE when shipping the feature).
- `AgentHub:MaxRunningSessionsPerOwner` (int, soft limit; sensible default e.g. 20).
- Helm/configmap wiring for both.

## Error handling

| Situation | Response |
| --- | --- |
| Missing/invalid token | `401`; MCP stable auth error, never echo token |
| Token ≠ parent id | `401`/`404` on internal routes |
| Soft limit exceeded | `429` with clear limit message |
| Invalid create payload | `400` via existing validation |
| In-pod get/delete non-descendant | `404` |
| `session_wait` timeout | Structured tool result, not a crash |
| Feature disabled | MCP not injected; internal spawn disabled (`404`/`403`) |
| Network/backend errors | Stable error codes; never leak tokens or child MCP secrets |

## Testing

**Backend (xUnit):** soft-limit; internal spawn auth; descendant delete (child, grandchild, sibling deny); `parentSessionId` round-trip; remote DELETE owner-only.

**Runtime MCP (Node):** tool schemas; fake HTTP happy paths; wait terminal vs timeout; no token in error strings; provider config injection when enabled.

**External package:** auth header + create/get/list/wait/delete against fake server.

## Security notes

- Personal API tokens stay out of pods; in-pod power is scoped to spawning/managing descendants of the current session under the same owner.
- Descendant check prevents a compromised agent from deleting arbitrary sibling sessions of the same user via the callback path (remote token path remains full owner power by design).
- Tool responses omit secrets (callback tokens, API tokens, mounted MCP env values).
