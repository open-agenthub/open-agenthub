# Usage tracking proof (#258, #282)

Usage tracking spans the agent CLIs, the session agent and the hub, and the unit tests fake every
boundary between them. The bugs fixed in #258 sat on those boundaries:
- Claude Code reports its price-table cost for a subscription login exactly as for an API key.
- Codex, OpenCode and OpenClaw keep their own usage records in their own formats.

So these scripts drive the pinned CLIs and the branch's real code against a mock model, and check
what the hub stores. `docs/usage-tracking.md` records the decisions they verify.

## Pieces

| File | What it is |
|---|---|
| `mock-model.mjs <port> <outdir>` | Messages, Responses and Chat Completions API. Every response reports input 1000 (600 cached) and output 50 (20 reasoning), plus a Claude cache write of 300. It logs the credential kind each request used (`x-api-key`, `oauth-bearer`), never its value, and stores OTLP bodies. A prompt containing `TICK` holds a Codex turn open for 40 s. |
| `otlp-dump.mjs <file>...` | Decodes the stored OTLP bodies. |
| `host/` | Throwaway ASP.NET host: the real Usage, OTLP and Internal controllers, the real Postgres stores and the real `UsageLimitService`. `POST /__seed` creates session rows (enums as numbers), `POST /__as/{user}` switches the signed-in user, and `POST /__can-start` asks the start gate. |
| `run-claude.sh` | One real `claude -p` turn exporting OTLP with the env a Claude pod gets. |
| `run-agent.sh codex\|opencode\|openclaw` | One real session through the branch's `agent-runtime/common/server.js` and the runtime's real driver. The CLI is pointed at the mock, and everything a session pod keeps on port 7681 or under `/tmp` is moved elsewhere. |
| `claude-auto-tui.mjs` | The interactive Claude TUI in a real PTY with both a login and a key: shows the "use this API key?" dialog. |
| `prove-billing.sh` | Five Claude turns covering subscription, API key, unattended Auto, interactive Auto and a payload that forges billing and owner. Checks which ones count as spend. |
| `prove-codex.sh` | Codex sessions, owner scoping, a forged token, and a restart on the kept `CODEX_HOME` (only the difference is added). |
| `prove-opencode-openclaw.sh` | OpenCode and OpenClaw sessions through `server.js`, then a long Codex turn reported on the 30-second tick while it runs. |
| `check-gate.sh` | Sets a personal limit below the recorded spend and asks the gate per agent and auth mode. |
| `browser-usage.cjs <cdp> <url\|-> <url\|-> <dir>` | Screenshots the Usage and Home pages through the session browser. |

## Run

Postgres 15 rootless on 127.0.0.1:54329 (see the `rootless-postgres-backend-tests` skill), one
fresh database per scenario. The pinned CLIs in a scratch prefix:
`npm i @openai/codex@0.160.0 opencode-ai@1.18.35 openclaw@2026.7.1-2`. Claude Code from the image.

```bash
dotnet build tests/UsageProof/host -o <bin>
ConnectionStrings__Postgres="Host=127.0.0.1;Port=54329;Username=postgres;Database=<db>" HOST_URL=http://0.0.0.0:8080 dotnet <bin>/host.dll &
node tests/UsageProof/mock-model.mjs 18780 <scratch>/mock &
ln -s /opt/session-agent/node_modules agent-runtime/session-agent/node_modules   # node-pty for server.js; do not commit
tests/UsageProof/prove-opencode-openclaw.sh "$PWD" <scratch> <cli-prefix>/node_modules/.bin
tests/UsageProof/check-gate.sh
```

For a before/after comparison, build the same host against an `origin/main` worktree: change the
`ProjectReference`, run it on port 8081 against the same database, and run
`check-gate.sh http://127.0.0.1:8081`.

## Recorded results (#282, 2026-10-25)

- **OpenCode and OpenClaw API-key sessions:** each recorded as 400 input, 600 cache read, 50 output, 20 reasoning and 1 turn, with $0.00213 as API spend.
- **OpenCode subscription session:** reports the same $0.00213 from its price table, and it is not counted as spend.
- **Long Codex turn:** nothing until t+30 s, then 400 input while still running (the tick report), then 800 after exit (the exit report).
- **Gate,** with a $0.003 limit against $0.00426 spent:

  | Start | Branch | `origin/main` |
  |---|---|---|
  | Claude, OpenCode or OpenClaw on an API key | refused (409) | only Claude refused |
  | Subscription or custom endpoint | allowed | allowed |
  | Codex or Cursor on an API key | allowed | allowed |

## Not covered

- The image's policy layers are not on the usage path and are left out: `/etc/codex`, the OpenCode policy plugin in `/etc/opencode`, and the OpenClaw exec policy.
- Interactive TUIs under `server.js` need a terminal client answering their queries. The tick path is shown with an autonomous Codex turn instead.
- Cursor cannot run without a real key, and its usage is not recorded.
