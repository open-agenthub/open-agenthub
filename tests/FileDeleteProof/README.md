# Deleting session files — real-path proof (#74)

A local-only test host, never a production service. It runs the shipped session file controllers,
file service, access service and share store on real Postgres and real S3-compatible storage. The
auth handler (a test user switched with `POST /__as/<user>`) and an unused session service are
the only stand-ins. It is `tests/FileTypesProof` with a user switch, shares by role
(`POST /__direct/<user>/<viewer|collaborator>`, `POST /__share/<role>`) and `GET /__objects`,
which lists the bucket with its own S3 client so "gone from storage" is the server's answer.

1. Start disposable Postgres on `127.0.0.1:54329` (user `postgres`, trust auth) and create the
   database `deleteproof`. Start disposable S3-compatible storage on `127.0.0.1:3910`, region
   `local`, bucket `delete-proof`, and put the key command output (`Key ID:` / `Secret key:`)
   in a private file outside the repository. The rootless setup is in the skills
   `rootless-postgres-backend-tests` and `rootless-s3-session-deletion-proof`.
2. `dotnet build tests/FileDeleteProof/Proof.csproj`
3. `PROOF_S3_KEY_FILE=<key file> node tests/FileDeleteProof/start.mjs` — loopback port 8080.
   No public storage url is configured, so browser uploads go through the API, as in a cluster
   whose storage is internal.
4. `node tests/FileDeleteProof/http.mjs` — owner, collaborator, viewer, unshared user and both
   link roles against delete and cancel, with the bucket checked after each step.
5. Start the frontend (`npx vite --host 0.0.0.0 --port 5173` in `frontend/`) and run
   `node tests/FileDeleteProof/browser.cjs <cdp endpoint> <frontend url> <screenshot dir>`:
   upload in the Files pane, open the in-row confirmation, Keep, then Delete; the row, the
   backend listing entry and the object are gone and no native dialog opened. Screenshots stay
   out of the repository.

Baseline: build the same host against unchanged main with
`-p:Backend=<main worktree>/backend/AgentHub.Api.csproj -o <dir>`, start it with
`PROOF_DLL=<dir>/Proof.dll PROOF_PORT=8081 PROOF_PG=<another database>`, and run
`PROOF_URL=http://127.0.0.1:8081 node tests/FileDeleteProof/http.mjs`. Main answers the
collaborator's delete with 403 (delete was owner-only) where the branch answers 204.
