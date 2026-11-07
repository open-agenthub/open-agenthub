# Provider execution history proof (#466, step 1)

This proves history composition and a preview, not runtime switching. Native histories are
created by the pinned CLIs, never by inserting native transcript or database rows. Model
responses, authentication and Kubernetes discovery are local stand-ins. Backend controllers,
sharing checks, Postgres, native stores/projections and `common/server.js` are real.

Prerequisites: .NET 10, Node 26, rootless Postgres on 54329 with database `proof466`, and the
runtime image's built dependencies at `/opt/session-agent/node_modules`. Install the exact
versions from the runtime Dockerfiles in a scratch prefix:

```sh
npm install --prefix /tmp/history-clis \
  @anthropic-ai/claude-code@2.1.318 @openai/codex@0.167.0 opencode-ai@1.18.35
PROOF_OUT=/tmp/history-capture PROOF_CLI_BIN=/tmp/history-clis/node_modules/.bin \
  node tests/ProviderSwitchHistoryProof/capture.mjs
```

The capture runs each interactive CLI in the production PTY server, sends six real turns
and reconnects its terminal socket. Each gets an isolated home and only a synthetic key.
It saves `claude.jsonl` and `codex.jsonl` outside the repository. It does not certify managed
tool approvals, which this history-only capture does not exercise.

Run the existing native OpenCode proof and keep its PTY server alive:

```sh
PROOF_OUT=/tmp/history-opencode OPENCODE_BIN=/tmp/history-clis/node_modules/.bin/opencode \
  PROOF_KEEP_RUNNING=1 PROOF_NODE_MODULES=/opt/session-agent/node_modules \
  node tests/OpenCodeConversationProof/run.mjs
```

It creates 111 real native turns, verifies tools, WAL reads and transport reconnect, and
serves the resulting native projection on 18564. See that proof's README for its limits.
With it still running, build/start this host in another terminal:

```sh
dotnet build tests/ProviderSwitchHistoryProof -o /tmp/history-host
PROOF_CAPTURE=/tmp/history-capture \
  PROOF_TRANSCRIPT=/tmp/history-opencode/transcript.jsonl AGENT_PORT=18564 \
  dotnet /tmp/history-host/Host.dll
PROOF_OUT=/tmp/history-results node tests/ProviderSwitchHistoryProof/verify.mjs
```

Create `PROOF_OUT` first. `PROOF_POSTGRES` overrides the connection string. The host uses
8080; the OpenCode proof uses 18561–18564, and Claude/Codex capture uses 18664. Run captures
before starting this host. No external provider access or credentials are needed.

The host seals the two independent histories using the internal ledger contract; OpenCode
is the current live segment. It deliberately leaves a stale legacy transcript in Postgres.
Seven pages (with the current scenario) retain all three sources for owners and shared
readers. Stored fallback, execution provenance, tool results, shared masking, revocation,
owner-only preview, omissions, framing, oversized text and terminal controls are asserted.

Restart the host against the same database, then rerun `verify.mjs`: the existing ledger is
read, not recreated, so this additionally verifies process restart readback. Delete the
scratch database to start a wholly new capture; do not reuse it with unrelated captures.
Generated results, native JSONL, logs and terminal screens stay in scratch space or Files.
Only these scripts and project sources belong in Git.

Run `SessionExecutionTests` and `LegacyExecutionMigrationTests` with `AGENTHUB_TEST_POSTGRES_CONNECTION_STRING` for real database
races, expected-generation rejection, owner scoping, return generations, rejected stale
snapshot writes, account/artifact references and deletion cascade. The legacy-migration tests
first verify the current schema rejects NULL native IDs, then simulate a partial older schema
with a real NULL row. They also force insert rejection and omit the ledger table to prove stored
and live native readback survive, warnings are logged, retries work, and cancellation propagates. Existing transcript and
remote/MCP tests cover the shared conversation path and context-clear boundaries.

Limits: no provider-switch API, new pod, volume mount, native context transfer, real target
login, target window admission or fault recovery is exercised. Those belong to steps 2–3.
