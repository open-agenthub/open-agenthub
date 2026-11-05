#!/usr/bin/env bash
# Placed first on PATH as `openclaw`. Every call goes to the real pinned CLI unchanged, except that
# right before the agent turn it (1) prints OpenClaw's own view of the MCP servers, from the exact
# environment the session agent gives the CLI, and (2) points the default model at the mock. The
# model switch is the only deviation from a real session: OpenClaw takes no custom endpoint from
# the session, and the proof must not reach a real provider.
set -euo pipefail
REAL=/usr/local/bin/openclaw
if [ "${1:-}" = "agent" ]; then
  # The session agent captures the CLI's output for the transcript, so this goes to a file the
  # proof prints afterwards.
  exec 3>&1 >>/tmp/openclaw-wrapper.log 2>&1
  echo "===== environment variable names the CLI was started with"
  env | cut -d= -f1 | sort | tr '\n' ' '; echo
  echo "===== openclaw mcp list"
  "$REAL" mcp list 2>&1 || true
  echo "===== openclaw mcp probe --json"
  "$REAL" mcp probe --json 2>&1 | node -e '
let s = ""; process.stdin.on("data", c => s += c); process.stdin.on("end", () => {
  const i = s.indexOf("{"); let j; try { j = JSON.parse(s.slice(i)); } catch { console.log(s); return; }
  console.log(JSON.stringify(j, (k, v) => k === "inputSchema" || k === "description" ? undefined : v, 1));
});' || true
  node -e '
const fs = require("fs"); const p = process.env.OPENCLAW_CONFIG_PATH;
const c = JSON.parse(fs.readFileSync(p, "utf8"));
c.models = { ...(c.models || {}), providers: { ...((c.models || {}).providers || {}), mock: {
  baseUrl: "http://127.0.0.1:18600/v1", apiKey: "mock-key", api: "openai-completions",
  models: [{ id: "m1", name: "m1" }] } } };
c.agents = c.agents || {}; c.agents.defaults = c.agents.defaults || {};
c.agents.defaults.model = { primary: "mock/m1" };
fs.writeFileSync(p, JSON.stringify(c, null, 2));'
  echo "===== openclaw $*"
  exec 1>&3 2>&1 3>&-
fi
exec "$REAL" "$@"
