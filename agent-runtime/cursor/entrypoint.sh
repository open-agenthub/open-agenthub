#!/usr/bin/env bash
set -euo pipefail

export AGENTHUB_STATE_DIR=.cursor
COMMON_ENTRYPOINT=/opt/session-agent/common/entrypoint-common.sh
if [ -f /opt/agenthub/session-agent/common/entrypoint-common.sh ]; then
  COMMON_ENTRYPOINT=/opt/agenthub/session-agent/common/entrypoint-common.sh
fi
source "$COMMON_ENTRYPOINT"

# The image cannot pin the CLI (the installer only ever serves latest), so the version
# that actually shipped is recorded at build time — surface it for anyone asking what
# this session is running.
if [ -z "${CURSOR_AGENT_VERSION:-}" ] && [ -f /usr/local/share/cursor-agent/INSTALLED_VERSION ]; then
  CURSOR_AGENT_VERSION="$(cat /usr/local/share/cursor-agent/INSTALLED_VERSION)"
  export CURSOR_AGENT_VERSION
fi

# CURSOR_CONFIG_DIR holds cli-config.json + mcp.json (official docs).
export CURSOR_CONFIG_DIR="${CURSOR_CONFIG_DIR:-$HOME/.cursor}"
mkdir -p "$CURSOR_CONFIG_DIR"
chmod 700 "$CURSOR_CONFIG_DIR"
umask 077

# File-store auth path discovered from Cursor Agent CLI 2026.07.23-e383d2b:
# domain "cursor" + AGENT_CLI_CREDENTIAL_STORE=file → auth.json under:
#   Darwin: ~/.cursor/auth.json
#   Linux:  ${XDG_CONFIG_HOME:-~/.config}/cursor/auth.json
# Keep a canonical copy under CURSOR_CONFIG_DIR so state-tar exclusion
# (stateDir/.authFilename) works, and symlink the CLI path to it on Linux.
export AGENT_CLI_CREDENTIAL_STORE=file
export NO_OPEN_BROWSER=1
case "$(uname -s)" in
Darwin)
  CURSOR_AUTH_FILE="$HOME/.cursor/auth.json"
  ;;
*)
  CURSOR_AUTH_DIR="${XDG_CONFIG_HOME:-$HOME/.config}/cursor"
  mkdir -p "$CURSOR_AUTH_DIR"
  chmod 700 "$CURSOR_AUTH_DIR"
  CURSOR_AUTH_FILE="$CURSOR_CONFIG_DIR/auth.json"
  ln -sfn "$CURSOR_AUTH_FILE" "$CURSOR_AUTH_DIR/auth.json"
  ;;
esac
export CURSOR_AUTH_FILE

# Policy before CLI start (official schema: version, permissions, approvalMode, sandbox).
node "$RUNTIME/cursor/cli-config.js" > "$CURSOR_CONFIG_DIR/cli-config.json"
chmod 600 "$CURSOR_CONFIG_DIR/cli-config.json"

# Always omit runtime-owned server names from user MCP so they cannot be spoofed.
if [ "${AGENTHUB_HAS_MCP:-0}" = "1" ] && [ -f /secrets/mcp/mcp.json ]; then
  node "$RUNTIME/cursor/mcp-config.js" /secrets/mcp/mcp.json agenthub_sessions agenthub_files agenthub_network > "$CURSOR_CONFIG_DIR/mcp.json"
  chmod 600 "$CURSOR_CONFIG_DIR/mcp.json"
elif [ "${AGENTHUB_SPAWN_MCP_ENABLED:-0}" = "1" ] || [ "${AGENTHUB_FILES_MCP_ENABLED:-0}" = "1" ] || [ "${AGENTHUB_NETWORK_MCP_ENABLED:-0}" = "1" ]; then
  printf '%s\n' '{"mcpServers":{}}' > "$CURSOR_CONFIG_DIR/mcp.json"
  chmod 600 "$CURSOR_CONFIG_DIR/mcp.json"
fi
if [ "${AGENTHUB_SPAWN_MCP_ENABLED:-0}" = "1" ]; then
  node "$RUNTIME/sessions/configure.mjs" "$CURSOR_CONFIG_DIR/mcp.json" "$CURSOR_CONFIG_DIR/mcp.json"
  chmod 600 "$CURSOR_CONFIG_DIR/mcp.json"
fi
if [ "${AGENTHUB_NETWORK_MCP_ENABLED:-0}" = "1" ]; then
  node "$RUNTIME/network/configure.mjs" "$CURSOR_CONFIG_DIR/mcp.json" "$CURSOR_CONFIG_DIR/mcp.json"
  chmod 600 "$CURSOR_CONFIG_DIR/mcp.json"
fi

if [ "${AGENTHUB_FILES_MCP_ENABLED:-0}" = "1" ]; then
  node "$RUNTIME/files/configure.mjs" "$CURSOR_CONFIG_DIR/mcp.json" "$CURSOR_CONFIG_DIR/mcp.json"
  chmod 600 "$CURSOR_CONFIG_DIR/mcp.json"
fi
# State restore always precedes authentication, and archived credentials are never trusted.
rm -f "$CURSOR_AUTH_FILE"
case "${AGENTHUB_AUTH_MODE:-}" in
apikey)
  if [ -z "${CURSOR_API_KEY:-}" ]; then
    echo "[entrypoint] ERROR: CURSOR_API_KEY is required for API-key authentication." >&2
    exit 1
  fi
  # API-key auth is env-driven (CURSOR_API_KEY); driver.prepare scopes it to the agent child.
  # No interactive `agent login` or subscription auth.json is required.
  ;;
subscription)
  AUTH_EXPECT_CREATE=1
  AUTH_BASELINE_SHA256=""
  if [ -f /secrets/cursor/auth.json ]; then
    cp /secrets/cursor/auth.json "$CURSOR_AUTH_FILE"
    chmod 600 "$CURSOR_AUTH_FILE"
    AUTH_BASELINE_SHA256="$(node -e '
const crypto = require("node:crypto");
const fs = require("node:fs");
process.stdout.write(crypto.createHash("sha256").update(fs.readFileSync(process.argv[1])).digest("hex"));
' "$CURSOR_AUTH_FILE")"
    AUTH_EXPECT_CREATE=0
    echo "[entrypoint] Cursor login restored from secret."
  fi

  if [ -n "${AGENTHUB_CALLBACK_URL:-}" ] && [ -n "${AGENTHUB_CALLBACK_TOKEN:-}" ]; then
    AGENTHUB_CURSOR_AUTH_EXPECT_CREATE="$AUTH_EXPECT_CREATE" \
      AGENTHUB_CURSOR_AUTH_BASELINE_SHA256="$AUTH_BASELINE_SHA256" \
      node "$RUNTIME/cursor/auth-watcher.js" &
  fi
  # Interactive subscription login runs in the session PTY via driver (not here).
  if [ ! -f "$CURSOR_AUTH_FILE" ] && [ "${AGENTHUB_MODE:-interactive}" = "interactive" ]; then
    export AGENTHUB_CURSOR_LOGIN=1
  fi
  ;;
*)
  echo "[entrypoint] ERROR: unsupported Cursor authentication mode." >&2
  exit 1
  ;;
esac

# Cursor reads standing instructions only from the workspace, so the caller's system prompt has to
# be a rule file there. Runs after the common entrypoint has cloned the repositories, otherwise the
# rule would be written into a directory the clone then replaces.
CURSOR_WORKDIR="${AGENTHUB_WORKDIR:-/workspace}"
[ -d "$CURSOR_WORKDIR" ] || CURSOR_WORKDIR="/workspace"
node "$RUNTIME/cursor/session-prompt.mjs" "$CURSOR_WORKDIR"

export AGENTHUB_DRIVER="$RUNTIME/cursor/driver.js"
echo "[entrypoint] Starting session-agent (mode=${AGENTHUB_MODE:-interactive}, resume=${AGENTHUB_RESUME:-0}, runtime=$RUNTIME)"
exec node "$RUNTIME/common/server.js"
