# Transcript masking proof (#245)

Run from the issue worktree with Node, the session runtime dependencies (including the built
PTY addon), curl, and .NET 10:

```sh
NODE_PATH=/opt/session-agent/node_modules \
DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=1 PROOF_DOTNET=/tmp/245-dotnet/dotnet \
node proofs/245/transport.cjs
```

The script copies the branch's actual runtime into an isolated directory, redirects its watcher,
archive and scrollback paths, and starts the real common/server.js. A small Node process in a
real PTY prints known file and environment values. A native JSONL file contains both too. No
masking, filesystem reads, HTTP responses, WebSocket replay, curl uploads or persistence timers
are mocked. The first HTTP/replay assertions happen before the real 30-second persistence tick.
The four uploaded bodies are retained in the printed temporary directory's captured.json.

The optional .NET program follows that server with the actual LiveTranscriptReader and
AgentPodRequests over HTTP, then reads the real uploaded bodies with TranscriptReader and
KubernetesSessionService. It invokes the owner/remote conversation controllers, remote transcript
controller, share transcript/conversation controller and MCP log/transcript methods. Persistence
and share authorization use narrow fixtures; controllers are invoked directly rather than through
an authenticated web host. The callback controller's relay notification passes through the
concrete KubernetesSessionService secret mask, fetching synthetic secret copies through the real
Kubernetes SDK over HTTP, then reaches a recording notifier. No external chat message is sent.

To reproduce the original failure without modifying the checkout:

```sh
git show main:agent-runtime/common/server.js > /tmp/245-main-server.js
PROOF_SERVER_SOURCE=/tmp/245-main-server.js NODE_PATH=/opt/session-agent/node_modules \
node proofs/245/transport.cjs
```

Main fails at the first live HTTP body assertion because both values are present. The fixed
transport passes, keeps raw source-byte cursors across marker length changes, and leaves the
native resume file unchanged. The regression suite also covers a projected file rotation and
mask expansion at the 8 MiB wire limit.

Recorded checks: 750 runtime tests, 63 focused common-server/masking tests, and 22 backend
transcript-reader tests passed. The main reproduction failed as expected; the fixed real
transport and backend consumer proof passed. This local rig does not establish kubelet projection
timing, provider CLI behavior, an integrated browser view, external relay delivery, or production
storage behavior. Those are outside the modified transport boundary.

Masking belongs at the pod's history outputs because every downstream history reader then receives
the same replacement. Rejected: per-controller masking, which misses other consumers, and rewriting
the native file, which corrupts the agent's resume context. Raw byte cursors remain independent of
the masked wire length; wire responses remain bounded because the backend enforces that bound.
