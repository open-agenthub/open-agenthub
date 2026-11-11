# Addressing shared sessions from the sessions MCP

A session acts as its authenticated owner for named sharing. The directory discovers candidates
through the existing sharing service and resolves each through ISessionAccessService again.
Reads and sends resolve user access on every request, so revocation applies to the next call.
Bearer links are never consulted. Inherited access uses the same access service as the web UI.
The owned project/ancestor/descendant scope continues to govern same-owner orchestration.
Project shares (#409) arrive the same way, with no MCP-specific code: a session in a project
shared with the caller's owner, or with one of their groups, is listed, read and messaged with the
role the project share gives, and leaves the directory on the next call once the share is removed
(`ProjectGrants_ReachTheMcpTools_ThroughTheSameDecision`).

Viewers may discover sessions and read sanitized status. Collaborators may also queue normal
messages. Cross-owner priority and interrupt are rejected: a grant to collaborate should not
stop the owner's current work. Allowing these flags, or silently downgrading them, would make
the sender's requested delivery misleading. Cross-owner messages wait in the recipient inbox
or its normal next-turn poll; no direct push is attempted.

Foreign reads use the shared-session sanitizer rather than full orchestration metadata, which
would expose prompts, credential selections and runtime configuration. Foreign delete, convert,
account operations and child creation retain their existing owner-bound ancestry checks.
Context clearing, secrets and helper containers remain scoped to the authenticated session;
sharing introduces no target parameter or foreign authority for those operations.

Messages belong to the recipient owner for history queries. Cross-owner messages snapshot the
sender owner and session title, so the receiver can identify the source even after renaming or
deletion. The trusted owner also appears in the title used by next-turn delivery. Resolving the
sender through the recipient's owned-session list was rejected because it cannot find foreign
senders. The body stays unchanged; sender identity is separate metadata.

A dedicated cross_owner_message_audit row records message id, sender/recipient owners and
session ids, and timestamp. The message and audit insert share one database statement; an
audit failure prevents message creation. No message body is copied into the audit table.
A generic role-change audit was rejected because its old/new-role columns cannot accurately
represent message delivery.

Account purge includes both parties' audit rows and sender snapshots, even if the sender's
session was already deleted. Relying only on the current session-id list would leave the new
owner/title metadata behind after the source session disappeared.


## External clients and explicit links

The remote `/mcp` tools and personal-token stdio server use the same named-share decision for
session and agent lists, status and collaborator messaging. Owned records are selected first;
using sharing-store projections for owned targets was rejected because those projections omit
fleet metadata. Entries carry `owner`, `accessRole` and `canWrite`; Viewer entries are readable
but cannot receive messages. Foreign priority and interrupt requests are rejected, never silently
downgraded. Management, credentials, context and container authority stay owner-scoped.

`share_link_read(link, offset?, max?)` and `share_link_send(link, message)` explicitly exercise a
bearer capability. `link` accepts the issued URL, `/shared/<token>` or the token itself. The
stdio server needs `AGENTHUB_URL` but no personal token for these tools. Remote clients without
an account connect to `/mcp/shared`, which exposes only these two tools. The authenticated
`/mcp` surface also includes them. Both routes use stateless transport: reusing a privileged MCP
session initialized on another route was rejected because endpoint authorization must not widen
an anonymous link request's tool collection.

The API bridge uses POST bodies on `/api/remote/share-link/read` and `/send`; a token never
becomes a route value or query parameter. Do not enable request-body/argument logging for these
bearer-secret calls. The tools neither fetch the supplied URL nor return the token. Invalid,
revoked, expired and unauthorized links answer not found. Access is resolved on each call;
redeeming a link never adds it to named discovery.

Read results contain `session` (sanitized status and link role) and `history` (the common
`ISharedSessionHistory` page used by the shared web view). Pass its `nextOffset` back as `offset`;
`-1` reads the latest window. `max` counts native entries or scrollback characters, according to
`history.source`. Reimplementing history in a tool was rejected because it would bypass live
history reads and masking from #427.

A Collaborator link can queue normal messages, with immutable `via share link` attribution and
an atomic cross-owner audit. Viewer links read only. There is no priority/interrupt parameter and
no management tool on the anonymous endpoint. The link is as powerful here as in the browser,
no more. An account identity is not inferred from a bearer link: that would falsely attribute an
anonymous holder to its issuer. The audit's nullable sender-session field represents external
clients, which have no originating session; user sends retain their owner, while link sends use
the anonymous attribution. Message bodies and tokens are absent from audit rows.

## Inbox delivery to legacy chat sessions (#568)

The legacy chat runtime has a live chat pipe, but no automatic inbox handoff between
turns. Named Collaborator sends and Collaborator link sends use the inbox. Normal
same-owner fleet sends also fall back to that inbox when the chat runtime declines
them. These sends now fail before inserting a message or a cross-owner audit row.
The error directs an owner or Collaborator to type into the session's conversation.

Using the owned priority push path for shared sends was rejected because it bypasses
the normal-priority sharing contract. A separate shared delivery path would need its
own acceptance and retry semantics before it could acknowledge a message. Existing
owned priority sends still use the live chat pipe. Previously queued rows stay in
the inbox and can still be retrieved through an explicit inbox read. There is no
migration, automatic replay, or deletion.
