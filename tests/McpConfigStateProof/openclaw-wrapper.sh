#!/usr/bin/env bash
# First on PATH as `openclaw`. Everything goes to the real CLI, except the agent turn itself:
# that records OpenClaw's own MCP view from the session agent's environment and exits, because
# this proof is about the config, not a model.
set -uo pipefail
REAL=/usr/local/bin/openclaw
if [ "${1:-}" = "agent" ]; then
  {
    echo "===== openclaw mcp list"
    "$REAL" mcp list 2>&1
    echo "===== openclaw mcp probe skill-library"
    "$REAL" mcp probe skill-library --json 2>&1 | node -e '
let s = ""; process.stdin.on("data", c => s += c); process.stdin.on("end", () => {
  try { const j = JSON.parse(s.slice(s.indexOf("{")));
    console.log(JSON.stringify({ tools: j.tools, diagnostics: j.diagnostics.map(d => d.message) }));
  } catch { console.log(s.slice(-400)); }
});' 
  } >> /tmp/openclaw-wrapper.log 2>&1
  exit 0
fi
exec "$REAL" "$@"
