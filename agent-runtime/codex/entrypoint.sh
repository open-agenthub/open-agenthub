#!/usr/bin/env bash
set -euo pipefail

export AGENTHUB_STATE_DIR=.codex
COMMON_ENTRYPOINT=/opt/session-agent/common/entrypoint-common.sh
if [ -f /opt/agenthub/session-agent/common/entrypoint-common.sh ]; then
  COMMON_ENTRYPOINT=/opt/agenthub/session-agent/common/entrypoint-common.sh
fi
source "$COMMON_ENTRYPOINT"

export CODEX_HOME="${CODEX_HOME:-$HOME/.codex}"
mkdir -p "$CODEX_HOME"
chmod 700 "$CODEX_HOME"
umask 077
printf '%s\n' 'cli_auth_credentials_store = "file"' > "$CODEX_HOME/config.toml"
if [ "${AGENTHUB_HAS_MCP:-0}" = "1" ] && [ -f /secrets/mcp/mcp.json ]; then
  node "$RUNTIME/codex/mcp-config.js" /secrets/mcp/mcp.json >> "$CODEX_HOME/config.toml"
fi
chmod 600 "$CODEX_HOME/config.toml"

# State restore always precedes authentication, and archived credentials are never trusted.
rm -f "$CODEX_HOME/auth.json"
case "${AGENTHUB_AUTH_MODE:-}" in
apikey)
  if [ -z "${CODEX_API_KEY:-}" ]; then
    echo "[entrypoint] ERROR: CODEX_API_KEY is required for API-key authentication." >&2
    exit 1
  fi
  if [ "${AGENTHUB_MODE:-interactive}" = "interactive" ]; then
    printf '%s\n' "$CODEX_API_KEY" | codex login --with-api-key
    unset CODEX_API_KEY
    [ ! -f "$CODEX_HOME/auth.json" ] || chmod 600 "$CODEX_HOME/auth.json"
  fi
  ;;
subscription)
  if [ -f /secrets/codex/auth.json ]; then
    cp /secrets/codex/auth.json "$CODEX_HOME/auth.json"
    chmod 600 "$CODEX_HOME/auth.json"
    echo "[entrypoint] Codex login restored from secret."
  fi

  if [ -n "${AGENTHUB_CALLBACK_URL:-}" ] && [ -n "${AGENTHUB_CALLBACK_TOKEN:-}" ]; then
    node "$RUNTIME/codex/auth-watcher.js" &
  fi
  if [ ! -f "$CODEX_HOME/auth.json" ] && [ "${AGENTHUB_MODE:-interactive}" = "interactive" ]; then
    codex login --device-auth
  fi
  ;;
*)
  echo "[entrypoint] ERROR: unsupported Codex authentication mode." >&2
  exit 1
  ;;
esac

export AGENTHUB_DRIVER="$RUNTIME/codex/driver.js"
echo "[entrypoint] Starting session-agent (mode=${AGENTHUB_MODE:-interactive}, resume=${AGENTHUB_RESUME:-0}, runtime=$RUNTIME)"
exec node "$RUNTIME/common/server.js"
