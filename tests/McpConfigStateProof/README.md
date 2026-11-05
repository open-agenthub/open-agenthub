# MCP config state proof (#446)

Shows, with the real `openclaw/entrypoint.sh`, `common/server.js`, the real state-archive script
and the pinned OpenClaw CLI, that a session's MCP header values no longer reach the state archive,
that OpenClaw still sends them, and that a resume keeps what it has to keep.

The session's MCP document holds the hub's own `skill-library` entry, with the local proxy
switched off, so it reaches OpenClaw as an HTTP server with a literal `X-Agent-Token` header.
`hub.mjs` stands in for the hub: its MCP endpoint refuses a request without that token, and it
serves `GET /state.tgz` for the restore. `openclaw-wrapper.sh` replaces only the agent turn, with
`openclaw mcp list` and `openclaw mcp probe` run in the session agent's environment.

Run 1 starts a fresh session, adds a server the way the agent would (`openclaw mcp set`) and
archives the state directory with `archiveScript` and the real driver's excludes. Run 2 resumes
from that archive in an empty home and archives again.

```bash
npm install -g --prefix "$S/oc" openclaw@<OPENCLAW_VERSION from the Dockerfile>
curl -fsSL -o "$S/bin/proot" https://proot.gitlab.io/proot/bin/proot && chmod +x "$S/bin/proot"
tests/McpConfigStateProof/run-local.sh <checkout> "$S" "$S/oc"
# Resume from an archive an older runtime wrote, instead of run 1:
LEGACY_STATE=<state.tgz> tests/McpConfigStateProof/run-local.sh <checkout> "$S" "$S/oc"
```

Run against an older checkout (copy this directory into it) for the baseline.

What to look for: `token occurrences` in `openclaw.json` and in the archive; `tokenOk: true` in
the hub log (the header OpenClaw actually sent); `agent_added` listed after the resume.

Codex and Cursor are covered by `agent-runtime/session-agent/test/mcp-config-state-archive.test.js`,
which runs their real converters and the real archive script.
