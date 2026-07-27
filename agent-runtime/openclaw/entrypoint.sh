#!/usr/bin/env bash
set -euo pipefail

export AGENTHUB_STATE_DIR=.openclaw
COMMON_ENTRYPOINT=/opt/session-agent/common/entrypoint-common.sh
if [ -f /opt/agenthub/session-agent/common/entrypoint-common.sh ]; then
  COMMON_ENTRYPOINT=/opt/agenthub/session-agent/common/entrypoint-common.sh
fi
source "$COMMON_ENTRYPOINT"

# OpenClaw state lives under ~/.openclaw (or $OPENCLAW_STATE_DIR).
export OPENCLAW_STATE_DIR="${OPENCLAW_STATE_DIR:-$HOME/.openclaw}"
mkdir -p "$OPENCLAW_STATE_DIR"
chmod 700 "$OPENCLAW_STATE_DIR"
umask 077

# PLACEHOLDER AgentHub credential file (auth.json + openclawAuth).
# Discovered OpenClaw 2026.7.1-2 model-auth locations (do not invent fields beyond these):
#   ~/.openclaw/agents/<agentId>/agent/auth-profiles.json  (logical JSON name)
#   ~/.openclaw/agents/<agentId>/agent/openclaw-agent.sqlite (canonical store)
# Store object shape: { version: 1, profiles: { ... }, order?: { ... } }.
# To re-discover after a CLI bump: npm pack openclaw@<ver> && inspect docs/concepts/oauth.md
# and dist/runtime-snapshots-*.js (AUTH_PROFILE_FILENAME) / dist/openclaw-agent-db.paths-*.js.
export OPENCLAW_AUTH_FILE="$OPENCLAW_STATE_DIR/auth.json"
export NO_OPEN_BROWSER=1

# State restore always precedes authentication, and archived credentials are never trusted.
rm -f "$OPENCLAW_AUTH_FILE"
case "${AGENTHUB_AUTH_MODE:-}" in
apikey)
  if [ -z "${ANTHROPIC_API_KEY:-}" ] && [ -z "${OPENAI_API_KEY:-}" ] && [ -z "${CURSOR_API_KEY:-}" ]; then
    echo "[entrypoint] ERROR: OpenClaw ApiKey mode requires ANTHROPIC_API_KEY, OPENAI_API_KEY, or CURSOR_API_KEY." >&2
    exit 1
  fi
  # API-key auth is env-driven; driver.prepare scopes keys to the agent child.
  ;;
subscription)
  AUTH_EXPECT_CREATE=1
  AUTH_BASELINE_SHA256=""
  if [ -f /secrets/openclaw/auth.json ]; then
    cp /secrets/openclaw/auth.json "$OPENCLAW_AUTH_FILE"
    chmod 600 "$OPENCLAW_AUTH_FILE"
    AUTH_BASELINE_SHA256="$(node -e '
const crypto = require("node:crypto");
const fs = require("node:fs");
process.stdout.write(crypto.createHash("sha256").update(fs.readFileSync(process.argv[1])).digest("hex"));
' "$OPENCLAW_AUTH_FILE")"
    AUTH_EXPECT_CREATE=0
    echo "[entrypoint] OpenClaw login restored from secret."
  fi

  if [ -n "${AGENTHUB_CALLBACK_URL:-}" ] && [ -n "${AGENTHUB_CALLBACK_TOKEN:-}" ]; then
    AGENTHUB_OPENCLAW_AUTH_EXPECT_CREATE="$AUTH_EXPECT_CREATE" \
      AGENTHUB_OPENCLAW_AUTH_BASELINE_SHA256="$AUTH_BASELINE_SHA256" \
      node "$RUNTIME/openclaw/auth-watcher.js" &
  fi
  # Interactive subscription login runs in the session PTY via driver (not here).
  if [ ! -f "$OPENCLAW_AUTH_FILE" ] && [ "${AGENTHUB_MODE:-interactive}" = "interactive" ]; then
    export AGENTHUB_OPENCLAW_LOGIN=1
  fi
  ;;
*)
  echo "[entrypoint] ERROR: unsupported OpenClaw authentication mode." >&2
  exit 1
  ;;
esac

export AGENTHUB_DRIVER="$RUNTIME/openclaw/driver.js"
echo "[entrypoint] Starting session-agent (mode=${AGENTHUB_MODE:-interactive}, resume=${AGENTHUB_RESUME:-0}, runtime=$RUNTIME)"
exec node "$RUNTIME/common/server.js"
