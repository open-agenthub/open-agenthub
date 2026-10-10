#!/usr/bin/env bash
set -euo pipefail

export AGENTHUB_STATE_DIR=.claude
COMMON_ENTRYPOINT=/opt/session-agent/common/entrypoint-common.sh
if [ -f /opt/agenthub/session-agent/common/entrypoint-common.sh ]; then
  COMMON_ENTRYPOINT=/opt/agenthub/session-agent/common/entrypoint-common.sh
fi
source "$COMMON_ENTRYPOINT"

AUTH_EXPECT_CREATE=1
AUTH_BASELINE_SHA256=""
if [ -f /secrets/claude/credentials.json ]; then
  mkdir -p "$HOME/.claude"
  cp /secrets/claude/credentials.json "$HOME/.claude/.credentials.json"
  chmod 600 "$HOME/.claude/.credentials.json"
  # The watcher compares against this instead of re-uploading what it just restored.
  AUTH_BASELINE_SHA256="$(node -e '
const crypto = require("node:crypto");
const fs = require("node:fs");
process.stdout.write(crypto.createHash("sha256").update(fs.readFileSync(process.argv[1])).digest("hex"));
' "$HOME/.claude/.credentials.json")"
  AUTH_EXPECT_CREATE=0
  echo "[entrypoint] Claude login restored from secret."
fi
CLAUDE_WORKDIR="${AGENTHUB_WORKDIR:-/workspace}"
[ -d "$CLAUDE_WORKDIR" ] || CLAUDE_WORKDIR="/workspace"
node "$RUNTIME/claude/workspace-trust.mjs" "$HOME/.claude.json" "$CLAUDE_WORKDIR"

# The session's MCP servers in Claude's user scope — central, outside the workspace, and usable
# from any directory. Runs after workspace-trust, which writes the same file. The agent itself is
# still launched with --mcp-config; this is what anything else running `claude` in the session sees,
# and unlike a project .mcp.json it needs no interactive approval.
node "$RUNTIME/claude/mcp-config.mjs" "$HOME/.claude.json" "${AGENTHUB_MCP_CONFIG:-}"

if [ -n "${AGENTHUB_CALLBACK_URL:-}" ] && [ -n "${AGENTHUB_CALLBACK_TOKEN:-}" ]; then
  AGENTHUB_CLAUDE_AUTH_EXPECT_CREATE="$AUTH_EXPECT_CREATE" \
    AGENTHUB_CLAUDE_AUTH_BASELINE_SHA256="$AUTH_BASELINE_SHA256" \
    node "$RUNTIME/claude/auth-watcher.js" &
  # Only the server gets SIGTERM on pod stop; it reads this to let the watcher flush first.
  echo "$!" > /tmp/agenthub-auth-watcher.pid
fi

AGENTHUB_RUNTIME="$RUNTIME/claude/hooks" \
  "$RUNTIME/claude/hooks/mcp-policy-hook.sh" --settings > "$HOME/.claude/settings.json"

# Materialize the user's skill library (own + shared + project) into ~/.claude/skills.
# Runs after the state restore so removed skills are cleaned up via the manifest.
if [ -n "${AGENTHUB_CALLBACK_URL:-}" ] && [ -n "${AGENTHUB_CALLBACK_TOKEN:-}" ]; then
  if curl -fsS -H "X-Agent-Token: $AGENTHUB_CALLBACK_TOKEN" "$AGENTHUB_CALLBACK_URL/skills" \
      | node "$RUNTIME/common/skills.js"; then
    echo "[entrypoint] Skills synced."
  else
    echo "[entrypoint] WARN: skills sync failed - continuing without library skills."
  fi
  # Upward sync: skills created inside earlier runs of this session (restored from
  # the state archive, unmanaged by the hub) are imported into the library.
  node "$RUNTIME/common/skills-sync-up.js" \
    || echo "[entrypoint] WARN: local skill import failed - continuing."
fi

export AGENTHUB_DRIVER="$RUNTIME/claude/driver.js"
echo "[entrypoint] Starting session-agent (mode=${AGENTHUB_MODE:-interactive}, resume=${AGENTHUB_RESUME:-0}, runtime=$RUNTIME)"
exec node "$RUNTIME/common/server.js"
