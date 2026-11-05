# External sessions MCP proof

Run this loopback-only host with .NET 10 and a disposable Postgres connection in
`ConnectionStrings__Postgres`, then run `RUNTIME=/opt/session-agent node tests/ExternalSessionsMcpProof/proof.mjs`.
The host runs production remote and callback controllers, personal-token lookup, sharing/access
services and message/audit stores. The owner-scoped session-service fixture reads real records;
no pod runs. The remote MCP endpoint uses the production tool class, with a fixture identity
established from the same personal token (it does not prove the production OAuth flow).

Owned peer/parent/child/sibling targets use full owner records, rather than partial sharing
projections. Every writable directory entry is sent a message. Viewer discovery remains read-only.
Generated output belongs outside Git. This proof uses synthetic owners and a disposable database.


The driver also starts anonymous stdio and Streamable HTTP clients on `/mcp/shared`, checks that
only the two link tools are exposed, and exercises Viewer/Collaborator status/history/send,
revocation and expiration. Link history passes through the real shared-history sanitizer.
Inspect the host log for every token recorded by the driver in
`/tmp/external-mcp-proof-tokens.txt`; no token may appear. Tokens are fixture capabilities and
are never committed. Expiry uses a loopback fixture database update to avoid a timing-based test.
