# Sharing a whole project: two-user Chromium proof (#409)

`Program.cs` runs the production `SessionsController`, `ProjectsController`, `SharingController`,
`ProjectSharingController` and `SharedSessionsController`. It uses the real `SessionAccessService`,
`SessionSharingService`, `ProjectSharingService` and `KubernetesSessionService`, and the real
Postgres session, user, group, project, permission and share stores. The stand-ins:

- a cookie (`proof-user`) that says who is signed in, in place of OIDC;
- group memberships written at startup the way a sign-in writes them (alice and carol in
  `release-team`, dave in `other-team`);
- a browser service that reports every browser as stopped, and an in-process Kubernetes API that
  lists nothing, so each session's phase is its stored status;
- `POST /api/proof/sessions`, which writes a session row as creating one does, without a pod.

Seed data: alice's project "Release" with `prep`, its child `notes` (started outside the project)
and `qa`; alice's `private` outside any project; carol's `planted`, which names alice's project id;
bob's own session.

`browser.cjs` drives four browser contexts. alice opens the project's share dialog from the
sidebar, picks bob from the suggestions (Viewer) and her group `release-team` (Collaborator).
The script then checks, without reloading:

- bob's sidebar shows `prep`, `notes` nested under it, and `qa`, marked "via project Release",
  and nothing else; the child opens with `inheritedFrom: prep`;
- carol gets Collaborator through the group, and dave gets nothing;
- a session alice creates in the project afterwards appears for bob;
- a session share lifts bob to Collaborator on `qa` and does not lower carol's role;
- removing bob in the dialog closes the project's sessions to him and keeps his own share on
  `qa`;
- deleting the project closes it for the group and leaves no share to list.

## Run

Postgres and .NET as in the `rootless-postgres-backend-tests` skill (TCP 54329). Chromium as in
`rootless-chromium-dotnet-proof` (headless shell from `browser-runtime`'s playwright-core; it also
needs `libxrender1`).

```sh
createdb -h 127.0.0.1 -p 54329 -U postgres proof409
dotnet build tests/ProjectSharingProof -o <scratch>/host && dotnet <scratch>/host/Host.dll   # :8080
(cd frontend && npx vite --port 5173 --strictPort)        # binds localhost; proxies /api to :8080
UI=http://localhost:5173 PW_MODULE=browser-runtime/node_modules/playwright-core \
  CHROME=<chrome-headless-shell> OUT=<scratch> node tests/ProjectSharingProof/browser.cjs
```

For the baseline, copy `Program.cs` and `Host.csproj` into a detached `origin/main` worktree and
build with `-p:DefineConstants=BASELINE`, which leaves out the project sharing types main does not
have. Use a fresh database, serve that worktree's frontend, and run the script with
`EXPECT=main`. It then expects no share action on the project and a 404 from the endpoint.
