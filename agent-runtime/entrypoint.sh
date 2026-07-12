#!/usr/bin/env bash
set -euo pipefail

# --- Determine the runtime directory ------------------------------------------
# Default image: /opt/session-agent. Custom image: the copy-runtime init container
# has copied session-agent, Node, and the Claude CLI to /opt/agenthub.
RUNTIME=/opt/session-agent
if [ -d /opt/agenthub/session-agent ]; then
  RUNTIME=/opt/agenthub/session-agent
  export PATH="/opt/agenthub/bin:$PATH"
fi

mkdir -p "$HOME/.ssh" "$HOME/.claude"

# --- Git identity ---
[ -f /secrets/creds/git_user_name ]  && git config --global user.name  "$(cat /secrets/creds/git_user_name)"
[ -f /secrets/creds/git_user_email ] && git config --global user.email "$(cat /secrets/creds/git_user_email)"

# --- SSH (for push/pull by the agent) ---
if [ -f /secrets/creds/ssh_key ]; then
  cp /secrets/creds/ssh_key "$HOME/.ssh/id"; chmod 600 "$HOME/.ssh/id"
  export GIT_SSH_COMMAND="ssh -i $HOME/.ssh/id -o IdentitiesOnly=yes -o UserKnownHostsFile=/secrets/creds/known_hosts -o StrictHostKeyChecking=yes"
  git config --global core.sshCommand "$GIT_SSH_COMMAND"
fi

# --- HTTPS remotes: connected-provider OAuth tokens (read/write), then manual PAT fallback ---
# Copy the credential store to a writable path (the secret mount is read-only, so git's
# store helper cannot lock it there). Rebuild the helper list idempotently.
git config --global --unset-all credential.helper 2>/dev/null || true
if [ -f /secrets/gitcreds/credentials ]; then
  cp /secrets/gitcreds/credentials "$HOME/.git-credentials" && chmod 600 "$HOME/.git-credentials"
  git config --global credential.helper store
fi
if [ -f /secrets/creds/gitlab_token ]; then
  git config --global --add credential.helper '!f() { echo "username=oauth2"; echo "password=$(cat /secrets/creds/gitlab_token)"; }; f'
fi

# --- Resume: restore saved Claude state from S3 ---
if [ "${AGENTHUB_RESUME:-0}" = "1" ] && [ -n "${AGENTHUB_STATE_GET_URL:-}" ]; then
  echo "[entrypoint] Downloading session state from S3 …"
  if curl -fsS -o /tmp/state.tgz "$AGENTHUB_STATE_GET_URL"; then
    tar xzf /tmp/state.tgz -C "$HOME" && echo "[entrypoint] State restored."
  else
    echo "[entrypoint] WARN: State download failed – starting without history."
  fi
fi

# --- Restore Claude subscription login (if saved previously) ---
if [ -f /secrets/claude/credentials.json ] && [ ! -f "$HOME/.claude/.credentials.json" ]; then
  cp /secrets/claude/credentials.json "$HOME/.claude/.credentials.json"
  chmod 600 "$HOME/.claude/.credentials.json"
  echo "[entrypoint] Claude login restored."
fi
# Skip the onboarding wizard if no Claude config file exists yet.
[ -f "$HOME/.claude.json" ] || printf '{"hasCompletedOnboarding": true}\n' > "$HOME/.claude.json"

# --- Login watcher: back up credential changes (first login, token refresh) to the backend ---
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

# --- Make the MCP config available to the project ---
if [ "${AGENTHUB_HAS_MCP:-0}" = "1" ] && [ -f /secrets/mcp/mcp.json ]; then
  TARGET="${AGENTHUB_WORKDIR:-/workspace}"
  [ -d "$TARGET" ] || TARGET="/workspace"
  cp /secrets/mcp/mcp.json "$TARGET/.mcp.json" || true
fi

# --- Register the Claude notification hook (fires n8n via the backend callback) ---
cat > "$HOME/.claude/settings.json" <<JSON
{
  "hooks": {
    "Notification": [
      { "hooks": [ { "type": "command", "command": "$RUNTIME/notify-hook.sh" } ] }
    ]
  }
}
JSON

echo "[entrypoint] Starting session-agent (mode=${AGENTHUB_MODE:-interactive}, resume=${AGENTHUB_RESUME:-0}, runtime=$RUNTIME)"
exec node "$RUNTIME/server.js"
