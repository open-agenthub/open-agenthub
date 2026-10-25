#!/usr/bin/env bash
# One real autonomous Codex session: the branch's REAL common/server.js with the REAL codex driver
# runs the pinned `codex exec` (real node-pty) against the mock model, and reports to the hub.
# Everything a session pod keeps under /tmp or on port 7681 is moved, so the rig cannot touch the
# pod it runs in (its own server, watcher and files).
# usage: run-codex-agent.sh <wt> <home> <session-id> <hub> <model-url> <codex-bin-dir> [prompt]
set -eu
WT=$1; H=$2; SID=$3; HUB=$4; MODEL=$5; BIN=$6; PROMPT=${7:-say hi}
mkdir -p "$H/.codex" "$H/work" "$H/files"
git -C "$H/work" init -q 2>/dev/null || true
cat > "$H/.codex/config.toml" <<TOML
model = "gpt-5.4"
model_provider = "rig"
[model_providers.rig]
name = "rig"
base_url = "$MODEL/v1"
wire_api = "responses"
env_key = "CODEX_API_KEY"
supports_websockets = false
request_max_retries = 0
[projects."$H/work"]
trust_level = "trusted"
TOML
cd "$H/work"
env -i PATH="$BIN:/usr/bin:/bin:/usr/local/bin" HOME="$H" CODEX_HOME="$H/.codex" CODEX_API_KEY=rig-key \
  NODE_PATH="$WT/agent-runtime/session-agent/node_modules" \
  AGENTHUB_DRIVER="$WT/agent-runtime/codex/driver.js" AGENTHUB_SESSION_ID="$SID" \
  AGENTHUB_MODE=autonomous AGENTHUB_PROMPT="$PROMPT" AGENTHUB_WORKDIR="$H/work" \
  AGENTHUB_CALLBACK_URL="$HUB/internal/sessions/$SID" AGENTHUB_CALLBACK_TOKEN="tok-$SID" \
  AGENTHUB_PORT=17681 AGENTHUB_FILE_ROOT="$H/files" AGENTHUB_AUTH_WATCHER_PIDFILE="$H/no-watcher.pid" \
  AGENTHUB_MCP_CONFIG= AGENTHUB_STATE_RESTORED=0 \
  timeout 120 node "$WT/agent-runtime/common/server.js"
