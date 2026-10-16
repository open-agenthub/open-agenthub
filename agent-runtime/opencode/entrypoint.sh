#!/usr/bin/env bash
set -euo pipefail

export AGENTHUB_STATE_DIR=.opencode
COMMON_ENTRYPOINT=/opt/session-agent/common/entrypoint-common.sh
if [ -f /opt/agenthub/session-agent/common/entrypoint-common.sh ]; then
  COMMON_ENTRYPOINT=/opt/agenthub/session-agent/common/entrypoint-common.sh
fi
source "$COMMON_ENTRYPOINT"

umask 077
OPENCODE_HOME="$HOME/.opencode"
mkdir -p "$OPENCODE_HOME"
chmod 700 "$OPENCODE_HOME"

# OpenCode keeps everything worth resuming - the session database, undo snapshots, the provider
# login - in its XDG data directory, ~/.local/share/opencode. The state archive takes exactly one
# directory under $HOME, so the data directory *is* ~/.opencode and the XDG path points at it.
# A link rather than XDG_DATA_HOME: that variable is read by every other tool in the session too,
# and would quietly move their data somewhere nothing archives.
mkdir -p "$HOME/.local/share"
if [ -d "$HOME/.local/share/opencode" ] && [ ! -L "$HOME/.local/share/opencode" ]; then
  rm -rf "$HOME/.local/share/opencode"
fi
ln -sfn "$OPENCODE_HOME" "$HOME/.local/share/opencode"
export OPENCODE_AUTH_FILE="$OPENCODE_HOME/auth.json"

# The CLI must not rewrite itself (the binary sits on the read-only root filesystem anyway, and a
# failed self-update is noise in the terminal) and must not publish sessions to a share link.
# Both are in the managed config too; the variables also cover an `opencode` the agent runs.
export OPENCODE_DISABLE_AUTOUPDATE=1
export OPENCODE_DISABLE_SHARE=1
export NO_OPEN_BROWSER=1

# State restore always precedes authentication, and archived credentials are never trusted.
rm -f "$OPENCODE_AUTH_FILE" "$OPENCODE_HOME/mcp-auth.json"
case "${AGENTHUB_AUTH_MODE:-}" in
apikey)
  if [ -z "${OPENCODE_API_KEY:-}" ]; then
    echo "[entrypoint] ERROR: OPENCODE_API_KEY is required for API-key authentication." >&2
    exit 1
  fi
  # API-key auth is env-driven (OPENCODE_API_KEY); driver.prepare scopes it to the agent child.
  ;;
subscription)
  AUTH_EXPECT_CREATE=1
  AUTH_BASELINE_SHA256=""
  if [ -f /secrets/opencode/auth.json ]; then
    cp /secrets/opencode/auth.json "$OPENCODE_AUTH_FILE"
    chmod 600 "$OPENCODE_AUTH_FILE"
    AUTH_BASELINE_SHA256="$(node -e '
const crypto = require("node:crypto");
const fs = require("node:fs");
process.stdout.write(crypto.createHash("sha256").update(fs.readFileSync(process.argv[1])).digest("hex"));
' "$OPENCODE_AUTH_FILE")"
    AUTH_EXPECT_CREATE=0
    echo "[entrypoint] OpenCode login restored from secret."
  fi

  if [ -n "${AGENTHUB_CALLBACK_URL:-}" ] && [ -n "${AGENTHUB_CALLBACK_TOKEN:-}" ]; then
    AGENTHUB_OPENCODE_AUTH_EXPECT_CREATE="$AUTH_EXPECT_CREATE" \
      AGENTHUB_OPENCODE_AUTH_BASELINE_SHA256="$AUTH_BASELINE_SHA256" \
      node "$RUNTIME/opencode/auth-watcher.js" &
  fi
  # Interactive subscription login runs in the session PTY via driver (not here).
  if [ ! -f "$OPENCODE_AUTH_FILE" ] && [ "${AGENTHUB_MODE:-interactive}" = "interactive" ]; then
    export AGENTHUB_OPENCODE_LOGIN=1
  fi
  ;;
*)
  echo "[entrypoint] ERROR: unsupported OpenCode authentication mode." >&2
  exit 1
  ;;
esac

# The caller's system prompt as an instructions file outside the workspace, so nothing lands in a
# tree the agent is about to commit. Rewritten (or removed) on every start, so a prompt from an
# earlier incarnation of this session cannot outlive the request that set it.
OPENCODE_USER_CONFIG_DIR="$HOME/.config/opencode"
mkdir -p "$OPENCODE_USER_CONFIG_DIR"
chmod 700 "$OPENCODE_USER_CONFIG_DIR"
export AGENTHUB_OPENCODE_INSTRUCTIONS="$OPENCODE_USER_CONFIG_DIR/agenthub-session.md"
node "$RUNTIME/opencode/session-prompt.mjs" "$AGENTHUB_OPENCODE_INSTRUCTIONS"

# User config: the session's MCP servers, the instructions file and the default model. The policy
# plugin is deliberately not here - it is registered in the managed config at /etc/opencode, which
# the agent cannot write, so it cannot be dropped by editing this file.
export AGENTHUB_OPENCODE_CONFIG="$OPENCODE_USER_CONFIG_DIR/opencode.json"
node "$RUNTIME/opencode/user-config.js" "$AGENTHUB_OPENCODE_CONFIG"
if [ ! -f /etc/opencode/opencode.json ]; then
  # Without the managed config there is no policy plugin, and every tool call would run unasked.
  echo "[entrypoint] ERROR: /etc/opencode/opencode.json is missing; refusing to start without the policy plugin." >&2
  exit 1
fi

# Materialize the user's skill library into ~/.claude/skills, which OpenCode reads natively.
if [ -n "${AGENTHUB_CALLBACK_URL:-}" ] && [ -n "${AGENTHUB_CALLBACK_TOKEN:-}" ]; then
  if curl -fsS -H "X-Agent-Token: $AGENTHUB_CALLBACK_TOKEN" "$AGENTHUB_CALLBACK_URL/skills" \
      | node "$RUNTIME/common/skills.js"; then
    echo "[entrypoint] Skills synced."
  else
    echo "[entrypoint] WARN: skills sync failed - continuing without library skills."
  fi
fi

export AGENTHUB_DRIVER="$RUNTIME/opencode/driver.js"
echo "[entrypoint] Starting session-agent (mode=${AGENTHUB_MODE:-interactive}, resume=${AGENTHUB_RESUME:-0}, runtime=$RUNTIME)"
exec node "$RUNTIME/common/server.js"
