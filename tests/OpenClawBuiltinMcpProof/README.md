# OpenClaw builtin MCP proof (#439)

Shows that an OpenClaw session registers the built-in `agenthub_browser`, `agenthub_sessions`,
`agenthub_files`, `agenthub_network` and `skill-library` servers, and that an agent turn can call
`agent_send` through them. Everything on the path is real — the runtime's `entrypoint.sh`,
`common/server.js` with the OpenClaw driver, the pinned OpenClaw CLI and the in-pod MCP servers —
except the hub (`hub.mjs`, a stand-in for the `/internal/sessions/{id}` callback API) and the
model (`model.mjs`, a Chat Completions mock that calls the first `*agent_send` tool it is offered).

`openclaw-wrapper.sh` sits first on `PATH` as `openclaw`. Right before the agent turn it records
`openclaw mcp list` and `openclaw mcp probe --json` from the exact environment the session agent
gives the CLI, and points the default model at the mock — the only deviation from a session,
because OpenClaw takes no custom endpoint and the proof must not reach a real provider.

The runtime is laid out from the `COPY … /opt/session-agent/…` lines of
`agent-runtime/openclaw/Dockerfile` into `/opt/agenthub/session-agent`, the path the platform
injects a runtime into a custom image at; `entrypoint-common.sh` switches to it when it exists.

## Run

In a session pod (no docker, no root), with the pinned CLI installed into a prefix:

```bash
npm install -g --prefix "$S/oc" openclaw@<OPENCLAW_VERSION from the Dockerfile>
curl -fsSL -o "$S/bin/proot" https://proot.gitlab.io/proot/bin/proot && chmod +x "$S/bin/proot"
tests/OpenClawBuiltinMcpProof/run-local.sh . "$S" "$S/oc"
```

`run-local.sh` binds the image paths with proot and relocates `/tmp` and the agent port, because
the session pod's own agent holds `/tmp/agenthub-mcp.json` and port 7681.

In the released image instead (`proof.sh <git sha>` downloads that commit and prints a "before"
listing from the image's own runtime first): run it as a helper container with emptyDir volumes
on `/opt/agenthub`, `/home/agent`, `/secrets/mcp` and `/workspace`. Give it memory — runs without
an explicit size were restarted mid-way and lost their logs.

## What to look for

- `openclaw mcp list` names all five servers; `openclaw mcp probe --json` lists their tools.
- The model log shows the `agenthub_*` tools offered and the `agent_send` tool result.
- The hub log shows `POST …/messages` with `tokenOk: true`, and the skill-library proxy's
  `initialize`/`tools/list` reaching `…/mcp`.
- `literal callback token occurrences in openclaw.json: 0`.
