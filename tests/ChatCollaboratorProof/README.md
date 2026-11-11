# Collaborator input in a chat-mode session: two-user Chromium proof (#495)

`Program.cs` runs the production `SessionsController`, `SharingController` and
`SharedSessionsController`, the real `SessionAccessService`, `SessionSharingService` and
`KubernetesSessionService`, the real Postgres session, user and share stores, and the terminal
WebSocket route exactly as `backend/Program.cs` maps it (`ResolveUserAsync`, then `TerminalProxy`
with `CanWriteTerminal`). Copied because those are local functions of the production `Program`.
The stand-ins:

- a cookie (`proof-user`) that says who is signed in, in place of OIDC;
- a Kubernetes API that reports one running pod, `session-pairing`, at 127.0.0.1, and a pod
  identity check that accepts loopback for that session.

The pod is real. `pod.mjs` runs the branch's `agent-runtime/common/server.js` with the real Claude
driver and the pinned Claude Code, with `AGENTHUB_UI_MODE` set as the pod spec sets it (`chat`
starts `claude -p --input-format stream-json`, `terminal` the TUI in a PTY). `mock-model.mjs`
answers each turn with `RECEIVED: <text>` and appends the text it received to `model.log`. The
proof reads what reached the agent from that log, not from any page.

`browser.cjs`: alice shares her running session with bob as Collaborator and with carol as Viewer
through the sharing API. bob's `/api/sessions/shared` must report the session's `uiMode`. alice,
then bob, opens the session at `/s/pairing` and sends a message from the composer. The script
records every frame each page sends on the terminal socket. carol must get no composer, and a
hand-made chat or input frame on her own socket must not reach the model.

## Cause, and the before/after

Before e9c4a3bc, `SessionShareStore.SessionColumns` did not select `sessions.ui_mode`. Every
shared projection (named, inherited, project, link) therefore reported `uiMode: "terminal"`, the
record's default. The Collaborator's view drew the terminal composer and sent keystrokes
(`{"type":"input"}`) to a pod that reads chat frames only, and those keystrokes were dropped.
Released 1.18.0 to 1.20.x behave this way. e9c4a3bc added the column while it was doing other
work, so the regression tests in `SessionShareStorePostgresTests` now pin it.

## Run

Postgres and .NET as in the `rootless-postgres-backend-tests` skill (TCP 54329), Chromium as in
`rootless-chromium-dotnet-proof`, Claude Code at the version pinned in `agent-runtime/claude/Dockerfile`
(`npm i --prefix <dir> @anthropic-ai/claude-code@<pin>`).

```sh
node tests/ChatCollaboratorProof/mock-model.mjs <scratch>/model 17695
REPO=$PWD CLAUDE_BIN_DIR=<dir>/node_modules/.bin node tests/ChatCollaboratorProof/pod.mjs <scratch>/pod chat 17681
createdb -h 127.0.0.1 -p 54329 -U postgres proof495
dotnet build tests/ChatCollaboratorProof -o <scratch>/host
PROOF_POSTGRES='Host=127.0.0.1;Port=54329;Username=postgres;Database=proof495' PROOF_UI_MODE=chat dotnet <scratch>/host/Host.dll
(cd frontend && npx vite --port 5173 --strictPort)
PW_MODULE=browser-runtime/node_modules/playwright-core CHROME=<chrome-headless-shell> OUT=<scratch> \
  MODEL_LOG=<scratch>/model/model.log EXPECT=after node tests/ChatCollaboratorProof/browser.cjs
```

Terminal comparison: start the pod with `terminal`, the host with `PROOF_UI_MODE=terminal` on a
fresh database, and run the script with `UI_MODE=terminal`.

Before: copy this directory into a detached worktree at `e9c4a3bc^`, build it with
`-p:DefineConstants=BASELINE` (that tree has no `TerminalProxy.ReadForUpgradeAsync`), serve that
worktree's frontend, use a fresh database, and run the script with `EXPECT=before`. The pod can stay
the same, because the pod was not where the bug was.

## Fleet and link sends to a live legacy chat session (#568)

The same host also serves the production callback send and share-link send endpoints,
including real fleet dispatch and message storage. `fleet.mjs` calls the actual in-pod
and external stdio MCP servers. It opens the production chat WebSocket as a
Collaborator and verifies composer messages before and after the fleet sends against
the mock model log. The host's proof-only message endpoint observes Postgres without
consuming the inbox.

Start the mock model and chat pod as above, using a fresh model log and a dedicated
Postgres database. The real pod can stay up across both runs because the fix is in
send-time validation. Start the host with `PROOF_URL=http://127.0.0.1:17680`, using
the same URL for the script. Make the runtime's Node dependencies available to the
two stdio servers, for example with links to `/opt/session-agent/node_modules` at
`agent-runtime/sessions/node_modules` and `mcp/agenthub/node_modules`.

Build the host against the baseline by copying this proof's `Program.cs` into
a checkout at `7784d872` and building that checkout's host project. From the
feature checkout, run:

```sh
touch <scratch>/model/model.log
node tests/ChatCollaboratorProof/fleet.mjs before <scratch>/model/model.log
```

Restart the host with the feature checkout's build against the same database, then run:

```sh
node tests/ChatCollaboratorProof/fleet.mjs after <scratch>/model/model.log
```

The baseline must add three pending rows (named Collaborator, Collaborator link,
and same owner). The fixed host must return `chat_inbox_unsupported` with actionable
guidance and add zero pending rows. The script compares existing rows by ID to verify
that the fix leaves the old inbox intact. Both runs deny both Viewer sends and
deliver a same-owner priority message to the real pinned CLI.
