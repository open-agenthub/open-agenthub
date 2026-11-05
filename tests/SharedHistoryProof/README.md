# Shared history beyond the stored tail: Chromium proof (#427)

`Program.cs` runs the production `SessionsController`, `SharingController` and
`SharedSessionsController`, the real `SessionAccessService`, `SessionSharingService`,
`SharedSessionHistory`, `KubernetesSessionService` and `LiveTranscriptReader` (through the real
`AgentPodRequests`, over real HTTP), and the real Postgres session, user and share stores.
The stand-ins:

- a cookie (`proof-user`) that says who is signed in, in place of OIDC;
- a Kubernetes API that reports one running pod, `session-reviews`, at 127.0.0.1, and 404 for
  everything else it is asked for by name;
- that pod: an HTTP server answering `GET /agenthub/transcript?from=N` the way
  `common/server.js` does (whole lines, at most 8 MiB, size and next offset in headers), with a
  pod identity check that accepts loopback for this session;
- no S3 (`NullArtifactStore`), so the stored transcript is only the Postgres tail capped at
  `ScrollbackLimits.MaxChars` — what a session has once its presigned upload url has expired.

Seed: alice's running session `reviews` with 401 turns (about 1.6 MB); the last one prints the
session's callback token and an MCP bearer credential from its inline MCP config. The stored
tail starts around turn 157.

`browser.cjs`: alice creates a Viewer link, an expiring Collaborator link and a named Viewer
share for bob. A cookieless context opens `/shared/<token>`, checks the newest turn is redacted,
clicks "Load earlier messages" until the first review of the day appears, and checks that neither
secret is anywhere on the page. bob reads the conversation from offset 0. Then alice deletes the
link (the open view switches to "invalid, expired, or revoked" on its next poll), the Collaborator
link is expired in the database, and bob's share is removed; each answers 404.

## Run

Postgres and .NET as in the `rootless-postgres-backend-tests` skill (TCP 54329), Chromium as in
`rootless-chromium-dotnet-proof`.

```sh
createdb -h 127.0.0.1 -p 54329 -U postgres proof427
dotnet build tests/SharedHistoryProof -o <scratch>/host && dotnet <scratch>/host/Host.dll   # :8080
(cd frontend && npx vite --port 5173 --strictPort)                                         # proxies /api
PW_MODULE=browser-runtime/node_modules/playwright-core CHROME=<chrome-headless-shell> OUT=<scratch> \
  node tests/SharedHistoryProof/browser.cjs
```

Baseline: copy this directory into a detached `origin/main` worktree, build with
`-p:DefineConstants=BASELINE` against a fresh database, serve that worktree's frontend and run the
same script. On main the link view starts at the stored tail and shows both secrets.
