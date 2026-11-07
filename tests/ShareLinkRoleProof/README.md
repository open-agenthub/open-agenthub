# Link role vs. person role: two-user Chromium proof (#519)

The share dialog used one role picker for "Add person" and "Create link", so a link created after
adding a Collaborator was a Collaborator link. `browser.cjs` runs against the
`tests/ProjectSharingProof` host (production sharing and shared-link controllers, real Postgres;
sign-in is the `proof-user` cookie) and Vite.

alice opens the share dialog of `private`, adds bob as Collaborator, and creates a link without
touching any link control. dave opens the link. The script checks:

- the link control still reads Viewer, and the button says "Create Viewer link" before creating;
- the backend stores the link as Viewer, the one-time banner and the link list name the role;
- dave gets `accessRole: Viewer`, `canWrite: false`, and the page shows "Read-only";
- reopening the dialog after picking Collaborator starts at Viewer again;
- control: a Collaborator link alice picks on purpose is labelled so, the picker falls back to
  Viewer afterwards, and dave can type through it — so the read-only check tells the two apart.

`EXPECT=main` runs the same clicks against main's frontend and expects the bug: the link is
Collaborator and its visitor can type.

## Run

Setup as in `tests/ProjectSharingProof/README.md`. Use a fresh database per run; the first check
expects nothing shared yet.

```sh
dotnet build tests/ProjectSharingProof -o <scratch>/host && PROOF_POSTGRES=... dotnet <scratch>/host/Host.dll
(cd frontend && npx vite --port 5173 --strictPort)
UI=http://localhost:5173 PW_MODULE=browser-runtime/node_modules/playwright-core \
  CHROME=<chrome-headless-shell> OUT=<scratch> EXPECT=branch node tests/ShareLinkRoleProof/browser.cjs
```

For the baseline, serve a detached `origin/main` worktree's frontend (its `node_modules` symlinked)
against the same host build and run with `EXPECT=main`.
