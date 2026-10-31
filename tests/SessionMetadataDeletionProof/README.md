# Session metadata deletion proof

The linked test sources run in the backend test project with real Postgres. Generated output
belongs outside Git.

```sh
export AGENTHUB_TEST_POSTGRES_CONNECTION_STRING='Host=127.0.0.1;Port=54329;Username=postgres;Database=postgres'
dotnet test tests/AgentHub.Api.Tests --filter FullyQualifiedName~SessionMetadataDeletion
```

The migration proof starts from unconstrained legacy tables containing both live rows and
orphans. It runs the production initializers twice, checks preservation and pruning, then
checks cascades. A rolled-back delete preserves metadata; writes creating new orphans fail.

The backend proof runs real controllers in a loopback web host, the real
`KubernetesSessionService`, real access resolution and real Postgres stores. The cluster
HTTP boundary answers as an empty namespace, browser operations and unrelated dependencies
are stand-ins, and the host supplies an authenticated owner and an enabled license.
It exercises web DELETE, remote token DELETE, internal child DELETE, the remote MCP tool,
expiry with the real advisory lock, account deletion and direct account purge. An anonymous
HTTP share request succeeds before each delete and returns 404 without detail afterward.
A separate owner's metadata survives. This does not exercise a deployed cluster, a real
identity provider, or MCP transport; the MCP production tool method executes directly.

Additional database proofs run simultaneous replica initialization and race each metadata
insert against an uncommitted registry delete, observing the real server lock before commit.

The expiry path also uses the real thread notifier and API client against a loopback HTTP
recorder. It snapshots the destination before deletion, posts exactly one expiry notice into
the original thread after registry and mapping removal, and removes the transient status.
An injected browser-cleanup failure produces no notice and preserves the mapping; a subsequent
successful sweep notifies once, and another sweep sends nothing.
