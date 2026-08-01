# MCP Catalog + OpenAPI/GraphQL Gateway Design

**Date:** 2026-08-01  
**Status:** Approved design  
**Branch / worktree:** `feat/mcp-catalog-api-gateway` @ `.worktrees/mcp-catalog-api-gateway`  
**Scope:** Personal + org MCP catalog, EE user/group ACL, session picker, AgentHub-hosted HTTP MCP gateway for OpenAPI/GraphQL. Skills library from WIP is **out of scope**.

## Objective

Let users (and admins) offer reusable MCP servers from a central catalog, attach them when creating a session, and turn an OpenAPI or GraphQL URL into an MCP the agent can call — by pasting a URL in Settings or in the session Advanced section.

## Decisions (from brainstorming)

| Topic | Choice |
| --- | --- |
| Session create UX | Advanced: library picker + URL paste + existing raw JSON |
| Catalog ownership | Personal library **and** admin org catalog |
| Entry kinds | OpenAPI/GraphQL URL wrapper **and** raw MCP config |
| Secrets | Hybrid: org entries may carry shared secrets; personal/shared consumers supply their own unless org shared-secret applies |
| Wrapper runtime | Shared AgentHub-side **HTTP MCP** gateway (not per-pod `npx`) |
| Ad-hoc URL in Advanced | Session-only ephemeral gateway registration; optional “Save to my library” |
| Approach | Extend WIP MCP library from `feature/skill-library-mcp` (**MCP only**, no skills) |

## Non-goals (v1)

- Skills library / skill MCP
- SSE MCP transport for Codex/Cursor (prefer HTTP / stdio)
- Automatic sync of upstream API changes beyond TTL cache + manual refresh
- Per-tool ACL inside a catalog entry (entry-level share only)
- Public anonymous MCP endpoints

## Architecture

```
Settings (personal / admin) ──► mcp_servers store ──► LibraryAccess (+ EE shares)
New Session Advanced ─────────► mcpServerIds + ephemeralApiSources + mcpConfigJson
                                         │
                                         ▼
                              McpConfigAssembler
                                         │
                    raw ─────────────────┼──► passthrough into session mcp.json Secret
                    api / ephemeral ─────┘──► HTTP MCP URL → Gateway
                                                      │
                                                      ▼
                                         fetch/cache spec → MCP tools
                                                      │
                                                      ▼
                                              upstream OpenAPI/GraphQL API
```

Agent pods never fetch OpenAPI/GraphQL specs themselves for catalog `api` entries. They connect to AgentHub’s streamable-HTTP MCP endpoint with a short-lived session-bound token.

## Data model

### `mcp_servers`

| Column | Notes |
| --- | --- |
| `id` | uuid |
| `owner` | user id, or `__org__` for admin catalog |
| `name` | MCP server key in `.mcp.json` |
| `description` | optional |
| `kind` | `raw` \| `api` |
| `config_json` | see below |
| `secret_json` | encrypted; never returned to non-owners |
| `created_at` / `updated_at` | |

**`raw` `config_json`:** single server entry (value side of `mcpServers`), e.g. `{"type":"http","url":"…"}` or stdio `command`/`args`/`env`.

**`api` `config_json`:**

```json
{
  "specType": "openapi" | "graphql" | "auto",
  "specUrl": "https://api.example.com/openapi.json",
  "baseUrl": "https://api.example.com",
  "auth": { "type": "none" | "bearer" | "header", "headerName": "Authorization" }
}
```

Secret material (`token`, `headerValue`, …) lives only in `secret_json`.

### EE shares

Reuse WIP pattern: `library_shares` with `item_type = mcp`, scopes `all` / users / groups. License-gated via `IEnterpriseLicense`.

### Sessions

- `mcp_server_ids` — JSON/text array of catalog ids  
- Keep `mcp_config` for inline overrides  
- Ephemeral API sources: **session-scoped gateway registrations** (not catalog rows), cleaned up when the session is deleted

### Org visibility

- **No EE license:** org catalog is admin-writable, readable/usable by all authenticated users; personal library is own-only (no sharing).  
- **With EE:** org and personal shares respect the share matrix; inaccessible ids fail strict resolve on create/update.

## APIs

| Method | Path | Notes |
| --- | --- | --- |
| GET/POST | `/api/mcp-servers` | List accessible / create personal |
| GET/PUT/DELETE | `/api/mcp-servers/{id}` | Owner (admin for `__org__`) |
| POST | `/api/mcp-servers/from-api` | Create `api` entry from URL |
| GET/PUT | `/api/ee/library/mcp-servers/{id}/shares` | EE ACL |
| CRUD | `/api/admin/mcp-servers` | Org catalog |
| POST/PUT | `/api/sessions` | `mcpServerIds`, `mcpConfigJson`, `ephemeralApiSources[]` |
| MCP HTTP | `/mcp/api/{id}` | Catalog `api` entry |
| MCP HTTP | `/mcp/session/{sessionId}/{name}` | Ephemeral session source |

Gateway auth: short-lived token in MCP HTTP headers, issued at pod spawn, bound to session id + allowed source. Reject if session inactive or access revoked.

## UI

### Settings → PERSONAL → MCP servers

- List own + shared-with-me + allowed org entries  
- Create tabs: **From API URL** | **Raw MCP config**  
- EE: share controls on own entries when licensed  
- Shared-from-others: read-only; no secrets

### Settings → ADMIN → Org MCP catalog

- Same create forms with `owner=__org__`  
- EE: per-entry ACL

### New / Edit Session → Advanced

1. Multi-select library picker (Mine / Org / Shared badges)  
2. Paste API URL → ephemeral gateway registration; checkbox “Save to my library”  
3. Existing raw MCP JSON textarea (inline wins on name clash)

## Gateway behavior

- Cache specs with TTL; Settings “Refresh spec” busts cache  
- OpenAPI: one MCP tool per operation  
- GraphQL: tools from introspection or schema URL (queries/mutations)  
- Proxy tool calls with configured auth  
- Upstream 4xx/5xx → MCP tool error string to the agent  
- Bad URL / parse failure at create → 400, do not save

## Secrets (hybrid)

| Entry | Who stores secret | Who uses it |
| --- | --- | --- |
| Org `api` with secret | Admin | Gateway injects for any allowed consumer |
| Personal `api` with secret | Owner | Gateway injects only for owner’s sessions (and EE consumers if we later allow “share including secret” — **v1: personal secrets are owner-only**; sharing an `api` entry without org shared-secret requires consumer to attach their own credential override, or use unauthenticated APIs) |
| Raw MCP with env tokens | Owner | Config (incl. secret refs) only returned to owner; shared raw entries without secrets; consumers may override via inline session JSON |

Encryption: ASP.NET Data Protection (or existing project equivalent) for `secret_json` at rest.

## Porting from WIP

Source worktree: `.worktrees/skill-library-mcp` (`feature/skill-library-mcp`).

**Port / adapt:** `McpServerStore`, `McpConfigAssembler`, `McpServersController`, `LibraryAccessService` (MCP methods), EE `LibraryShareStore` (MCP item type), `McpServersPane`, session picker, related tests.

**Do not port:** Skills store/controllers/UI/embeddings/search.

Extend models with `kind`, `secret_json`, `__org__` owner, admin controller, gateway, ephemeral sources.

## Testing

- Access: own / org (no license) / EE shares / strict resolve  
- Assembler merge order (inline wins)  
- `from-api` create + ephemeral session path  
- Gateway auth reject/allow  
- OpenAPI + GraphQL fixture smoke tests  
- Frontend: picker + Settings panes (vitest)

## Reference

WIP MCP library: `feature/skill-library-mcp`  
Existing session MCP mount: `KubernetesSessionService` + `AgentPodSpecFactory` + agent-runtime converters  
EE share precedent: allowed-agents + usage group roles (`UserGroupStore` / OIDC groups)
