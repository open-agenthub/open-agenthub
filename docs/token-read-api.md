# Token reads reuse the interactive access decisions

Clients holding personal tokens need project selection, attachable library ids and session history
without an interactive login. Separate read routes reuse the existing conversation and library
services instead of admitting personal tokens to all interactive routes. That alternative would
open unrelated mutations and configuration responses. Sharing remains in its existing service;
conversation alone accepts a licensed user grant, while inbox history remains owner only.

All routes below use `Authorization: Bearer oah_…`, have no request body, return JSON with 200,
and return 401 for a missing/invalid token. Credential-scope decisions are in
[credential-scopes.md](credential-scopes.md).

| Method and path | Query | Response | Other status codes |
|---|---|---|---|
| `GET /api/remote/projects` | none | `[{id,name,color,sortOrder}]` | none |
| `GET /api/remote/projects/{id}` | none | `{id,name,color,sortOrder}` | 404 unknown/foreign project |
| `GET /api/remote/mcp-servers` | none | `[{id,name}]` | none |
| `GET /api/remote/sessions/{id}/messages` | `cursor` opaque, `limit` 1..100 (default 20) | `{messages:[{id,from,fromTitle,body,createdAt,deliveredAt,priority,interrupt,deliveredVia}],nextCursor}` | 400 `invalid_message_page`; 404 unknown/foreign session |
| `GET /api/remote/sessions/{id}/conversation` | `offset`, `max` | `{sessionId,phase,running,source,offset,nextOffset,length,truncated,entries,text}` | 400 malformed integer; 404 unknown/inaccessible session or absent transcript |

Messages are newest first; `nextCursor` is null at the end. A stable timestamp/id keyset avoids
skipping messages created together or repeating rows when new messages arrive between pages.
Offset paging was rejected because arrivals shift every later page. Reads never acknowledge inbox
messages. Sender titles come only from the caller's owned sessions.

Conversation is the exact Workspace `ConversationPage`: return `nextOffset` unchanged as `offset`.
For `source=native`, `entries` contains `{role,text,at,tool}` transcript entries and `text` is empty;
for `source=scrollback`, entries is empty and text contains terminal output. The cursor counts entries
or characters respectively; treat it as opaque. Native `max` defaults to 500 and clamps to 1..5000;
scrollback uses the transcript character limits. Owned running sessions use the live reader with
stored fallback; shared reads follow the existing stored-conversation policy. A shrinking `length`
means restart from zero. `running=false` means polling may stop.

The stdio tools are `projects_list`, `project_get`, `mcp_servers_list`, `session_messages` and
`session_conversation`, with the same parameters and responses. The API contract fixture directory
contains a miniature external spec for gateway tests, not a snapshot of this API; these routes are
therefore documented here rather than added to that unrelated fixture.
