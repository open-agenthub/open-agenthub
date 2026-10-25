#!/usr/bin/env bash
# One real autonomous session: the branch's REAL common/server.js with the REAL driver of <agent>
# runs the pinned CLI (real node-pty) against the mock model and reports to the hub, as in a pod:
#   codex     `codex exec`              usage from $CODEX_HOME/sessions/**/rollout-*.jsonl
#   opencode  `opencode run`            usage from ~/.opencode/opencode.db
#   openclaw  `openclaw agent --local`  usage from ~/.openclaw/agents/*/sessions/*.jsonl
# Each CLI is pointed at the mock with its own provider config; the policy layers of the image
# (/etc/codex, /etc/opencode, the OpenClaw exec policy) are not part of this path and are left out.
# Everything a session pod keeps under /tmp or on port 7681 is moved, so the rig cannot touch the
# pod it runs in (its own server, watcher and files).
# usage: [MODE=interactive] [TIMEOUT=s] run-agent.sh <codex|opencode|openclaw> <wt> <home> <session-id> <hub> <model-url> <cli-bin-dir> [prompt]
# MODE=interactive runs the TUI with the prompt auto-submitted; it reports on the 30-second
# persistence tick while running, and TIMEOUT ends it.
set -eu
AGENT=$1; WT=$2; H=$3; SID=$4; HUB=$5; MODEL=$6; BIN=$7; PROMPT=${8:-say hi}
mkdir -p "$H/work" "$H/files"
EXTRA=()
case "$AGENT" in
  codex)
    mkdir -p "$H/.codex"
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
    EXTRA=(CODEX_HOME="$H/.codex" CODEX_API_KEY=rig-key)
    ;;
  opencode)
    # The entrypoint makes ~/.opencode OpenCode's data directory by linking the XDG path to it.
    mkdir -p "$H/.opencode" "$H/.local/share" "$H/.config/opencode"
    ln -sfn "$H/.opencode" "$H/.local/share/opencode"
    cat > "$H/.config/opencode/opencode.json" <<JSON
{"\$schema":"https://opencode.ai/config.json","model":"mock/m1","permission":"allow","autoupdate":false,"share":"disabled",
 "provider":{"mock":{"npm":"@ai-sdk/openai-compatible","name":"Mock","options":{"baseURL":"$MODEL/v1","apiKey":"rig-key"},
  "models":{"m1":{"name":"M1","tool_call":true,"cost":{"input":3,"output":15,"cache_read":0.3,"cache_write":3.75}}}}}}
JSON
    EXTRA=(OPENCODE_DISABLE_AUTOUPDATE=1 OPENCODE_DISABLE_SHARE=1)
    ;;
  openclaw)
    mkdir -p "$H/.openclaw"
    cat > "$H/.openclaw/openclaw.json" <<JSON
{"models":{"mode":"merge","providers":{"rig":{"baseUrl":"$MODEL/v1","api":"openai-completions","apiKey":"rig-key",
  "models":[{"id":"m1","name":"M1","reasoning":false,"input":["text"],"cost":{"input":3,"output":15,"cacheRead":0.3,"cacheWrite":3.75},"contextWindow":128000,"maxTokens":4096}]}}},
 "agents":{"defaults":{"model":{"primary":"rig/m1"},"workspace":"$H/work"}}}
JSON
    ;;
  *) echo "unknown agent $AGENT" >&2; exit 2 ;;
esac
cd "$H/work"
env -i PATH="$BIN:/usr/bin:/bin:/usr/local/bin" HOME="$H" "${EXTRA[@]}" \
  NODE_PATH="$WT/agent-runtime/session-agent/node_modules" \
  AGENTHUB_DRIVER="$WT/agent-runtime/$AGENT/driver.js" AGENTHUB_SESSION_ID="$SID" \
  AGENTHUB_MODE="${MODE:-autonomous}" AGENTHUB_PROMPT="$PROMPT" AGENTHUB_WORKDIR="$H/work" \
  AGENTHUB_CALLBACK_URL="$HUB/internal/sessions/$SID" AGENTHUB_CALLBACK_TOKEN="tok-$SID" \
  AGENTHUB_PORT=17681 AGENTHUB_FILE_ROOT="$H/files" AGENTHUB_AUTH_WATCHER_PIDFILE="$H/no-watcher.pid" \
  AGENTHUB_MCP_CONFIG= AGENTHUB_STATE_RESTORED=0 \
  timeout "${TIMEOUT:-240}" node "$WT/agent-runtime/common/server.js"
