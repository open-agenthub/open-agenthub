# Model discovery proof (#461)

Scripts that run the pinned agent CLIs' own model-list sources against a recording mock of the
provider's service, so account switches, refusals and endpoint lists can be observed without a
real login. `docs/design/model-discovery.md` records what they established. Generated logs,
screenshots and outputs stay out of the repository.

## Pieces

- `mock.mjs <port> <log> <rules.json> [bundled-models.json]` — answers `/…/models` per caller
  (account id, key or bearer) from `rules.json`; `{ "status": 403 }` refuses. The log keeps only
  the first 12 characters of a credential. `start-mock.sh <port>` (re)starts it with a pidfile.
  `bundled-models.json` is the pinned Codex binary's embedded catalogue (the `{"models": [` object
  with `"slug"` entries), extracted so the mock serves real-shaped rows.
- `mkacct.sh <CODEX_HOME> <account>` — a fake Codex subscription login pointed at the mock.
- `codex-models.mjs`, `claude-models.mjs` — the raw CLI requests used while researching.
- `codex-standin-driver.js` — the real Codex driver with only its interactive process replaced:
  the pinned TUI exits at bootstrap against the mock, and an exited agent refuses credential
  installs. Credential install and the catalog are the real driver's.

## Runs

```bash
npm i @openai/codex@0.167.0 opencode-ai@1.18.35         # into <clis>; claude at /usr/local/bin
./start-mock.sh 18181
node session-agent-e2e.mjs <worktree> <clis>/node_modules/.bin      # 11 checks
node cli-probes.mjs <worktree> <bin with claude, opencode, agent>    # 13 checks
```

`browser-dialog.mjs` (18 checks) needs, in addition: rootless Postgres, `host/` built and running
on 127.0.0.1:8080 (`ConnectionStrings__Postgres=…;Pooling=false AGENT_PORT=27681 dotnet host.dll`,
then `POST /__seed?token=proof-callback-token-0123456789`), the session agent on 27681 with
`codex-standin-driver.js`, account 1's login and `AGENTHUB_CALLBACK_URL=http://127.0.0.1:8080/internal/sessions/proof-session`,
Vite on 0.0.0.0:5173, and a Chromium CDP endpoint:
`NODE_PATH=<dir with playwright-core> node browser-dialog.mjs <cdp> http://<pod ip>:5173/ <out> <dir with auth-acct-{1,2,deny}.json> <mock log>`.
