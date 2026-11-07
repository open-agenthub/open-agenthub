# Model switch proof (#478)

Scripts that establish what `docs/design/live-model-switch.md` records: how each pinned CLI
switches its model mid-conversation, and that the Workspace switch works end to end. Every
script talks to a mock model (`fixtures.mjs`) that answers `MODEL=<model in the request>
CONTEXT=KEPT|NONE` — whether the first turn's marker word is still in the request — and logs only
the URL, the model and that flag, never a header. Generated output (screens, logs, screenshots,
results) stays out of the repository.

## Setup

```bash
npm i @openai/codex@0.167.0 opencode-ai@1.18.35 openclaw@2026.7.1-2   # into <clis>; claude 2.1.318 at /usr/local/bin
npm i @xterm/headless@5.5.0                                            # into <xterm>
export CLIS=<clis> XTERM=<xterm>
```

node-pty and `ws` come from the session agent's install (`/opt/session-agent/node_modules`).

## `cli-switch.mjs` — the CLI on its own

One pinned CLI in a real PTY, driven by steps:

```bash
node cli-switch.mjs claude '[{"ready":"for shortcuts|auto mode"},{"paste":"Remember the word PAPAYA42."},
  {"ready":"MODEL="},{"paste":"/model claude-sonnet-5-5"},{"ready":"Switch model?","ms":5000},{"keys":"\r"},
  {"ready":"Set model to"},{"paste":"Which word?"},{"ready":"MODEL=claude-sonnet"},{"dump":"turn2"}]' out-claude
```

`START_MODEL` sets the model the CLI starts on, `CUSTOM_ENDPOINT=1` gives Codex the runtime's
custom provider block, and `REUSE=<earlier outdir>` with `EXTRA_ARGS='["resume","--last","--model","mock-b"]'`
is the restart half: the same HOME and work dir, the CLI's resume arguments.

## `session-agent-e2e.mjs` — the switch through the session agent

The branch's real `common/server.js` and driver start the pinned CLI; turns go over the agent
socket the way the Workspace composer sends them, and the switch through `POST /agenthub/model`
the way the hub sends it.

```bash
node session-agent-e2e.mjs codex out-e2e-codex      # claude | codex | opencode | openclaw
```

It prints, as JSON: the switch answer and the CLI's confirmation line, the model of the next
turn and whether it still carried the first, the refusal while a turn runs (and that nothing was
typed for it), and the refusals of an invalid id and a wrong token. Not covered here: the turn
hooks the real entrypoints install (Codex's `/etc/codex`, OpenCode's `/etc/opencode`), so in
these runs the session agent judges "a turn runs" from the CLI's output. The hook path is covered
by `common-server-model-switch.test.js`.

The session agent is started with its own pidfile and `AGENTHUB_STATE_RESTORED=0`: in a session
pod, the defaults name this pod's own auth watcher and restore marker. The runtime path is
`PROOF_RUNTIME`, not `RUNTIME`, which a session pod already exports for its own session agent.

## `browser-proof.mjs` — the Workspace picker in Chromium

Needs, beyond the setup above: rootless Postgres, the proof host, Vite, and Chromium.

```bash
dotnet build host/host.csproj -o <host-bin>
ConnectionStrings__Postgres="Host=127.0.0.1;Port=54329;Username=postgres;Database=postgres;Pooling=false" \
  AGENT_PORT=27681 HOST_PORT=8081 dotnet <host-bin>/host.dll &
(cd ../../frontend && npx vite --port 5173 --strictPort) &
PLAYWRIGHT_CORE=<browser-runtime>/node_modules/playwright-core CHROME=<chrome-headless-shell> node browser-proof.mjs out-browser
```

`host/` runs the REAL `SessionModelsController` and `SessionModelSwitch` on the real Postgres
stores and sharing rules; Kubernetes is replaced only by "the pod runs on 127.0.0.1". The script
starts the real session agent with the real Codex CLI, a stand-in hub on `localhost:8080` (both
loopback families: Vite's proxy resolves `localhost` to `::1` first) that forwards the model routes
to the host and the terminal socket to the session agent, and then checks the owner's switch, the
next turn, the refusal during a running turn, a viewer, a collaborator and the 390 px layout.
The seeded row keeps the callback token of its first insert, so the token is fixed.
