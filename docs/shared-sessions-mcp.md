# Addressing shared sessions from the sessions MCP

A session acts as its authenticated owner for named sharing. The directory discovers candidates
through the existing sharing service and resolves each through ISessionAccessService again.
Reads and sends resolve user access on every request, so revocation applies to the next call.
Bearer links are never consulted. Inherited access uses the same access service as the web UI.
The owned project/ancestor/descendant scope continues to govern same-owner orchestration.

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
