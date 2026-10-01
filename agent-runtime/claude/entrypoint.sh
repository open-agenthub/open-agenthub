#!/usr/bin/env bash
set -euo pipefail

export AGENTHUB_STATE_DIR=.claude
COMMON_ENTRYPOINT=/opt/session-agent/common/entrypoint-common.sh
if [ -f /opt/agenthub/session-agent/common/entrypoint-common.sh ]; then
  COMMON_ENTRYPOINT=/opt/agenthub/session-agent/common/entrypoint-common.sh
fi
source "$COMMON_ENTRYPOINT"

if [ -f /secrets/claude/credentials.json ]; then
  mkdir -p "$HOME/.claude"
  cp /secrets/claude/credentials.json "$HOME/.claude/.credentials.json"
  chmod 600 "$HOME/.claude/.credentials.json"
  echo "[entrypoint] Claude login restored from secret."
fi
CLAUDE_WORKDIR="${AGENTHUB_WORKDIR:-/workspace}"
[ -d "$CLAUDE_WORKDIR" ] || CLAUDE_WORKDIR="/workspace"
node "$RUNTIME/claude/workspace-trust.mjs" "$HOME/.claude.json" "$CLAUDE_WORKDIR"

if [ -n "${AGENTHUB_CALLBACK_URL:-}" ] && [ -n "${AGENTHUB_CALLBACK_TOKEN:-}" ]; then
  (
    CREDS="$HOME/.claude/.credentials.json"
    CACHE=/tmp/.claude-creds-uploaded
    [ -f "$CREDS" ] && cp "$CREDS" "$CACHE" 2>/dev/null || true
    while true; do
      if [ -f "$CREDS" ] && ! cmp -s "$CREDS" "$CACHE" 2>/dev/null; then
        if curl -fsS -X PUT -H "X-Agent-Token: $AGENTHUB_CALLBACK_TOKEN" \
             -H "Content-Type: application/json" \
             --data-binary @"$CREDS" "$AGENTHUB_CALLBACK_URL/claude-credentials"; then
          cp "$CREDS" "$CACHE"
          echo "[entrypoint] Claude login backed up."
        fi
      fi
      sleep 30
    done
  ) &
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
