# Shared sessions in the list, inherited by their children: two-user Chromium proof (#407, #410)

`Program.cs` runs the production `SessionsController`, `SharingController` and
`SharedSessionsController`, the real `SessionAccessService`, `SessionSharingService` and
`KubernetesSessionService`, and the real Postgres session, user, project, permission and share
stores. The stand-ins:

- a cookie (`proof-user`) that says who is signed in, in place of OIDC;
- a browser service that reports every browser as stopped;
- an in-process Kubernetes API that lists no pods, cron jobs or secrets, so each session's phase
  falls back to its stored status, exactly as for a session without a pod.

No session pod starts, so nothing here proves terminal traffic. Opening a session is checked
through the real `GET /api/sessions/{id}` and the rendered header.

Seed data: alice's `release` → `changelog` → `links` chain, a `link-only` session, a `review`
session with an open question, a session of carol's whose parent id names alice's `release`,
and bob's own session.

`browser.cjs` drives two browser contexts, alice and bob. alice shares `release` with bob as
Collaborator, `review` as Viewer, and makes a link for `link-only`. The script then checks,
without reloading, that bob's sidebar shows the chain nested with role, owner and "child of …".
It also checks that the link-only session and carol's planted session are missing, that the
viewer's question does not count as waiting on bob, and that the child opens with the inherited
role. Last, alice revokes the parent share, and parent and children leave bob's list and return
404.

## Run

Postgres and .NET as in the `rootless-postgres-backend-tests` skill (TCP 54329). Chromium as in
`rootless-chromium-dotnet-proof` (headless shell from `browser-runtime`'s playwright-core).

```sh
createdb -h 127.0.0.1 -p 54329 -U postgres proof407
dotnet build tests/SharedSessionListProof -o <scratch>/host && dotnet <scratch>/host/Host.dll   # :8080
(cd frontend && npx vite --port 5173 --strictPort)                                              # proxies /api to :8080
PW_MODULE=browser-runtime/node_modules/playwright-core CHROME=<chrome-headless-shell> OUT=<scratch> \
  node tests/SharedSessionListProof/browser.cjs
```

For the baseline, copy `Program.cs` and `Host.csproj` into a detached `origin/main` worktree.
Build there against a fresh database, serve that worktree's frontend, and run the script with
`EXPECT=main`. The script then expects the children to be missing.
