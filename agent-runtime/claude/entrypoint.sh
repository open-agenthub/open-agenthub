#!/usr/bin/env bash
set -euo pipefail

export AGENTHUB_STATE_DIR=.claude
COMMON_ENTRYPOINT=/opt/session-agent/common/entrypoint-common.sh
if [ -f /opt/agenthub/session-agent/common/entrypoint-common.sh ]; then
  COMMON_ENTRYPOINT=/opt/agenthub/session-agent/common/entrypoint-common.sh
fi
source "$COMMON_ENTRYPOINT"

# Updates belong to image rollouts; a background installer cannot write the pinned CLI.
export DISABLE_AUTOUPDATER=1

AUTH_EXPECT_CREATE=1
AUTH_BASELINE_SHA256=""
CUSTOM_ENDPOINT=0
[ "${AGENTHUB_AUTH_MODE:-}" = "customendpoint" ] && CUSTOM_ENDPOINT=1
if [ "$CUSTOM_ENDPOINT" = "1" ]; then
  # A self-hosted server instead of a login (docs/custom-model-endpoints.md). Fails the start with
  # a reason the session list shows when the server cannot serve Claude Code, rather than letting
  # the CLI retry against it until the session looks hung.
  node "$RUNTIME/common/model-endpoint.js" probe messages
elif [ -f /secrets/claude/credentials.json ]; then
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

# No watcher on a custom endpoint: there is no login to write back, and one the agent created in
# the session would be uploaded as the owner's Claude login.
if [ "$CUSTOM_ENDPOINT" = "0" ] && [ -n "${AGENTHUB_CALLBACK_URL:-}" ] && [ -n "${AGENTHUB_CALLBACK_TOKEN:-}" ]; then
  AGENTHUB_CLAUDE_AUTH_EXPECT_CREATE="$AUTH_EXPECT_CREATE" \
    AGENTHUB_CLAUDE_AUTH_BASELINE_SHA256="$AUTH_BASELINE_SHA256" \
    node "$RUNTIME/claude/auth-watcher.js" &
  # Only the server gets SIGTERM on pod stop; it reads this to let the watcher flush first.
  echo "$!" > /tmp/agenthub-auth-watcher.pid
fi

AGENTHUB_RUNTIME="$RUNTIME/claude/hooks" \
  "$RUNTIME/claude/hooks/mcp-policy-hook.sh" --settings > "$HOME/.claude/settings.json"

sync_skill_library

# The agenthub-fleet mod (docs/claude-code-mods.md) delivers pushed fleet messages into the
# CLI. The token guards the loopback queue it polls on the session agent; minted per pod and
# shared with the CLI process only (both inherit it from here), never with the hub. The plugin
# directory is resolved through $RUNTIME so a custom image, which gets the runtime copied to
# /opt/agenthub, loads the mod from there — the image's own ENV would point into nothing.
FLEET_MOD="$RUNTIME/claude/mods/agenthub-fleet"
if [ -d "$FLEET_MOD" ]; then
  export CLAUDE_CODE_PLUGIN_DIRS="$FLEET_MOD"
  export AGENTHUB_MOD_TOKEN="$(node -e 'process.stdout.write(require("node:crypto").randomBytes(24).toString("hex"))')"
fi

export AGENTHUB_DRIVER="$RUNTIME/claude/driver.js"
echo "[entrypoint] Starting session-agent (mode=${AGENTHUB_MODE:-interactive}, resume=${AGENTHUB_RESUME:-0}, runtime=$RUNTIME)"
exec node "$RUNTIME/common/server.js"
