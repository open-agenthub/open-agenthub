# File types real-path proof

This is a local-only test host, never a production service. It uses the shipped backend
controllers, authorizer, access service, project-file rule and file service, real Postgres and
real object storage. The synthetic auth handler selects the test owner; unrelated session
lifecycle and licence dependencies are stand-ins.

1. Start disposable Postgres on `127.0.0.1:54329` (database `postgres`, user `postgres`, trust
   auth), and disposable S3-compatible storage on `127.0.0.1:3910` with region `local`, bucket
   `file-proof`. Give its local test key read/write access; put the key command output containing
   `Key ID:` and `Secret key:` in a private file outside the repository.
2. `dotnet build tests/FileTypesProof/Proof.csproj`
3. `PROOF_S3_KEY_FILE=<private key file> node tests/FileTypesProof/start.mjs`
   (`DOTNET` selects the SDK executable). The host binds only loopback and uses synthetic tokens.
4. `node tests/FileTypesProof/http.mjs` checks uploads, completion, response headers, byte equality,
   own/sibling reads, and oversized reservations. Storage has a public endpoint configured too,
   so owner downloads also prove SVG/opaque files bypass redirects.
5. `node tests/FileTypesProof/mcp.mjs` starts two shipped files MCP processes over stdio. Set
   `RUNTIME` to a directory with the session-agent package and dependencies. It uploads SVG,
   arbitrary binary and extensionless files, reads them by path and fetches them from a sibling.
6. Start the frontend Vite server on port 5173 and run
   `node tests/FileTypesProof/browser.cjs <cdp endpoint> <Vite URL reachable from browser>`.
   `PROOF_RUNTIME` can select the directory containing `node_modules/playwright-core`; otherwise
   it uses `/opt/session-agent`.

Generated inputs, caches and file IDs stay in ignored `*.local` directories/files.
Use disposable services: the host seeds sessions `s1`/`s2` and the proof retains their uploads.
The rootless service setup is documented in the session-files real-media proof skill.

For the original failure, build this host against unchanged main using
`-p:Backend=<absolute main worktree>/backend/AgentHub.Api.csproj`, then start that assembly with
`PROOF_DLL=<path to Proof.dll> PROOF_PORT=8081`. SVG, ZIP and extensionless reservations return
400 `unsupported_file_type`; the same requests on the branch return ready files. No source
replacement, stash, real user credentials or cluster is needed.
