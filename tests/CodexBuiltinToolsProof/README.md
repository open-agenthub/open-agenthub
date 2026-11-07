# Built-in session tools in Codex sessions (#504)

`proof.mjs` creates Codex sessions through the real backend (dev auth, real kube-apiserver and
Postgres; rig as in `tests/SessionResourcesProof/README.md`, plus `SessionContainers__Enabled=true`
and a stored synthetic OpenAI key), reads the pod spec the backend wrote, and runs the pinned CLI
with that pod's environment and callback token. The MCP server is the real `agenthub_sessions`
server, configured by the runtime's own `mcp-config.js --builtin`; the hook is the real
`policy-hook.js` behind a recorder. A mock model searches the deferred MCP tools (Codex 0.167
offers them through `tool_search`) and calls `agent_inbox`, `container_run`, `container_list` and
`container_stop`.

Unattended sessions run `codex exec` with the driver's arguments; the hooks are loaded from
`CODEX_HOME/config.toml`. Interactive sessions run the TUI with the driver's arguments in a PTY,
with the shipped `requirements.toml` bound over `/etc/codex` and a runtime copy over
`/opt/session-agent` by proot (5.4, `PROOT_NO_SECCOMP=1`, started with a newer C library's loader
when the host's is too old; 5.1 crashes hook subprocesses).

```sh
RUNTIME=<dir with codex/ sessions/ network/ common/ and a node_modules link> \
CODEX_BIN=<pinned codex> PROOT_BIN=<proot launcher> KUBECONFIG=<admin> KUBECTL=kubectl \
OUT=<dir> [CASES=1,3] [FAULT=slow-once] [RUNTIME_SOURCE=<release agent-runtime>] node proof.mjs
```

`RUNTIME_SOURCE` runs another release's driver, hook and requirements (export a tag with
`git archive <tag> agent-runtime`) against today's backend. `FAULT=slow-once` routes the session's
callbacks through a proxy that holds the first `/agent-policy` request for `container_stop` 4 s.

## Results

| Runtime | Session | agent_inbox | container_run | container_list | container_stop |
|---|---|---|---|---|---|
| main | autonomous, auto-approve on | ran | ran | ran | ran |
| main | autonomous, auto-approve off | blocked | blocked | blocked | blocked |
| main | auto-approve off, `allowedMcpTools: [container_run]` | blocked | ran | blocked | blocked |
| main | auto-approve off, legacy `allowedTools: [mcp__…container_run]` | blocked | blocked | blocked | blocked |
| main, TUI | interactive, auto-approve on (± allow list) | ran | ran | ran | ran |
| v1.16.0 (Codex 0.166.0), TUI | interactive, auto-approve on | ran | ran | ran | ran |
| v1.16.0, TUI, `FAULT=slow-once` | interactive, auto-approve on | ran | ran | ran | **blocked** |
| branch, TUI, `FAULT=slow-once` | interactive, auto-approve on | ran | ran | ran | ran |
| branch | auto-approve off, `allowedMcpTools: [container_run]` | ran | ran | ran | ran |
| branch | auto-approve off, legacy `allowedTools: [mcp__…container_run]` | ran | ran | ran | ran |
| branch | autonomous, auto-approve off | ran | blocked | ran | ran (allowed; 404, nothing was started) |

Not covered: a real kubelet (helper containers are created as objects only), the real image
filesystem, and OpenCode and Claude under the CLI (their decisions are covered by the controller
and matcher tests).
