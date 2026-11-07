# OpenCode native conversation proof (#462)

Run with the exact `OPENCODE_VERSION` from `agent-runtime/opencode/Dockerfile` installed in a
scratch prefix. Node 26 and the runtime's `node-pty`, `ws` and `@xterm/headless` are required.
No history rows are injected into the native store: the real OpenCode server creates all turns.
Only model replies and hub policy callbacks are deterministic local stand-ins.

```sh
npm install --prefix <scratch>/cli opencode-ai@1.18.35
OPENCODE_BIN=<scratch>/cli/node_modules/.bin/opencode \
PROOF_OUT=<scratch>/run PROOF_KEEP_RUNNING=1 \
PROOF_NODE_MODULES=/opt/session-agent/node_modules \
node tests/OpenCodeConversationProof/run.mjs
```

The script uses OpenCode's managed-config test directory (a read-only config file), runs one
real gated tool and 110 further native turns, stops the native server, then resumes that session
in the real common/server.js PTY with the real interactive CLI. A headless terminal answers
terminal queries. It verifies the first and last turns, tool calls/results, native message
count, and duplicate-free history after reconnect. The native SQLite database uses the runtime
entrypoint's data-directory symlink layout. Default plugins, external model discovery and file
watching are disabled to keep unrelated network downloads outside this proof.

For shared reads, start rootless Postgres on 54329 and create `proof462` (see the
`rootless-postgres-backend-tests` skill), then:

```sh
dotnet build tests/OpenCodeConversationProof -o <scratch>/host
PROOF_TRANSCRIPT=<scratch>/run/transcript.jsonl \
AGENT_PORT=18564 PROOF_URL=http://127.0.0.1:8080 \
dotnet <scratch>/host/Host.dll
PROOF_OUT=<scratch>/run node tests/OpenCodeConversationProof/shared.mjs
```

The host uses production sharing/access controllers, production session/share stores against
real Postgres, `KubernetesSessionService`, `AgentPodRequests`, `LiveTranscriptReader` and
`SessionTranscripts`. Authentication and the cluster API are stand-ins. Transcript bytes are
read over HTTP from the running real session transport, rather than a stand-in pod. Synthetic
callback/MCP credentials appear in native model output to verify the existing masking. Link
and named viewers page the native history, see tool results, and lose access on revocation.

Outputs (`results.json`, `shared-results.json`, `terminal.txt`, `transcript.jsonl`, logs) belong
in the scratch directory or Files, never Git. Stop the proof process with SIGTERM after shared
checks. Ports 18561–18564 and 8080 must be free. Use a fresh output directory for each run.

Focused regression checks also exercise a real SQLite engine for torn journal writes, native
writer locks, incompatible schemas, oversized fields, masking before clipping, and mutable
assistant messages. The transport/backend tests cover projection identity changes without
inventing conversation clears or reusing a browser cursor against different earlier entries.
