# Shared sessions MCP and owned fleet proof (#408, #421)

This host runs the production InternalController, callback-token authorizer, access/sharing
services and Postgres stores. Two synthetic owners each have a session. The Node driver starts
two copies of the real sessions stdio MCP and sends A's message to B's inbox. It checks viewer
reads, collaborator sends and attribution, priority/interrupt denial, management denials,
revocation and link-only denial, for both the named target and its inherited child. The Postgres
suite additionally checks atomic auditing, audit cleanup, strongest-grant precedence and live
downgrade from inherited collaborator to direct viewer.

The ISessionService fixture projects owned records from the real session store for list/read
operations and rejects other operations. The fixture seeds an owned project peer, sibling,
parent and child, plus an excluded session in another project. The driver verifies every listed
owned target except self can be read and sent to, and reads each message through its real stdio
inbox. It does not establish a running CLI, pod, push injection or external login provider. Grant changes use a loopback-only
fixture endpoint backed by the real share store; that endpoint is not a product API.

Use a disposable Postgres database, .NET 10 and the shipped session-agent Node dependencies:

```sh
export ConnectionStrings__Postgres='Host=127.0.0.1;Port=54329;Username=postgres;Database=proof408'
# Create the database first. Set LD_LIBRARY_PATH if using unpacked Postgres/.NET libraries.
dotnet run --project tests/SharedSessionsMcpProof/Host.csproj
# In another terminal, point RUNTIME to a directory with the shipped node_modules.
RUNTIME=/opt/session-agent node tests/SharedSessionsMcpProof/proof.mjs
AGENTHUB_TEST_POSTGRES_CONNECTION_STRING="$ConnectionStrings__Postgres" \
  dotnet test tests/AgentHub.Api.Tests --filter FullyQualifiedName~SharedSessionsMcpPostgresTests
```

Keep generated logs and results outside the repository. Only proof sources are versioned.

For the unchanged-main comparison, copy Host.csproj and Program.cs into a separate baseline
worktree, remove the InternalController constructor's access/sharing arguments, and change its
listen port to 8409. Run that host against the same disposable schema, then run:

```sh
BASELINE=1 PROOF_URL=http://127.0.0.1:8409 node tests/SharedSessionsMcpProof/proof.mjs
```

The baseline driver asserts the original failure: a named viewer cannot discover/read B and a
named collaborator cannot send to B. Run the normal driver again against the changed host after
this comparison; the fixture mutates actual grants.

## Owned fleet regression decision

The sharing store returns a partial session record without ProjectId. Using that record for an
owned target's fleet check rejected unrelated project peers while the parent chain still worked.
Owned targets now use the owner-scoped session store and the same fleet selector for directory,
read and send. Foreign targets continue through the access service and its role checks. Inbox
reads authenticate only the addressed session's callback; they do not resolve a peer target.
Owned directory entries include the authenticated owner instead of leaving owner null.

Expanding the sharing projection was rejected: it would leave owned authorization dependent on
an unrelated sharing projection and would not align the former descendant-only status route.
Management routes deliberately retain their descendant-only boundary. Viewer shares remain
read-only directory entries; the directory's self entry cannot receive a self-message.

For the #421 before/after comparison, run this fixture and driver against the unchanged
controller first (no BASELINE flag). The owned sibling/project peer send must fail, while
SharedSessionsMcpPostgresTests has one owned-fleet failure and its existing cross-owner tests
pass. Restore the changed controller, rebuild the host, and run both again. All owned-fleet and
cross-owner assertions must pass. Use a disposable database; the fixture initializes project
and group stores because current sharing queries depend on those schemas.
