# Recipient suggestions in the share dialogs: two-user Chromium proof (#470)

`browser.cjs` runs against the `ProjectSharingProof` host: the production sharing, project
sharing, sessions and shared-session controllers, the real recipient search, and real Postgres
stores, with a `proof-user` cookie in place of sign-in. See `../ProjectSharingProof/README.md`
for what is real and what is a stand-in. The seeded accounts' display names equal their
usernames, which is the case where the old click changed nothing at all.

Session dialog, by mouse: alice types `bob` (his full username), clicks the suggestion, sets
Collaborator and presses Add. The script checks that the click selects bob, closes the list and
does not share by itself, that Add shares with the chosen role, and that bob's sidebar then shows
the session and it opens for him as Collaborator.

Project dialog, keyboard only: alice types `car`, ArrowDown highlights the first suggestion
(`aria-activedescendant`), Escape closes the list and leaves the dialog open, ArrowDown and Enter
select carol, a second Enter shares; carol's sidebar then shows the project's sessions.

## Run

Set up Postgres, .NET and Chromium as in `../ProjectSharingProof/README.md`, then:

```sh
createdb -h 127.0.0.1 -p 54329 -U postgres proof470
dotnet build tests/ProjectSharingProof -o <scratch>/host
PROOF_POSTGRES="Host=127.0.0.1;Port=54329;Username=postgres;Database=proof470" dotnet <scratch>/host/Host.dll
(cd frontend && npx vite --port 5173 --strictPort)
UI=http://localhost:5173 PW_MODULE=browser-runtime/node_modules/playwright-core \
  CHROME=<chrome-headless-shell> OUT=<scratch> node tests/RecipientPickerProof/browser.cjs
```

For the baseline, serve the frontend of a detached `origin/main` worktree against a fresh
database and run with `EXPECT=main`: it expects the click to leave the text unchanged, the list
open and no selection. The backend is unchanged by #470, so the same host serves both runs.
Screenshots go to `OUT`; do not commit them.
