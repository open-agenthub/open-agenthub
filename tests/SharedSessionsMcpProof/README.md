# Shared sessions MCP proof (#408)

This host runs the production InternalController, callback-token authorizer, access/sharing
services and Postgres stores. Two synthetic owners each have a session. The Node driver starts
two copies of the real sessions stdio MCP and sends A's message to B's inbox. It checks viewer
reads, collaborator sends and attribution, priority/interrupt denial, management denials,
revocation and link-only denial. The Postgres suite additionally checks the atomic audit row.

The ISessionService fixture returns an empty owned-session list and rejects other operations.
This isolates foreign-session authorization: it does not establish a running CLI, Kubernetes
pod, push injection or production identity-provider login. Grant changes use a loopback-only
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
