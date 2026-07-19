#!/usr/bin/env bash
set -euo pipefail

RUNTIME=/opt/session-agent
if [ -d /opt/agenthub/session-agent ]; then
  RUNTIME=/opt/agenthub/session-agent
  export PATH="/opt/agenthub/bin:$PATH"
fi
export RUNTIME

: "${AGENTHUB_STATE_DIR:?AGENTHUB_STATE_DIR is required}"
mkdir -p "$HOME/.ssh" "$HOME/$AGENTHUB_STATE_DIR"

[ -f /secrets/creds/git_user_name ]  && git config --global user.name  "$(cat /secrets/creds/git_user_name)"
[ -f /secrets/creds/git_user_email ] && git config --global user.email "$(cat /secrets/creds/git_user_email)"

if [ -f /secrets/creds/ssh_key ]; then
  cp /secrets/creds/ssh_key "$HOME/.ssh/id"; chmod 600 "$HOME/.ssh/id"
  export GIT_SSH_COMMAND="ssh -i $HOME/.ssh/id -o IdentitiesOnly=yes -o UserKnownHostsFile=/secrets/creds/known_hosts -o StrictHostKeyChecking=yes"
  git config --global core.sshCommand "$GIT_SSH_COMMAND"
fi

git config --global --unset-all credential.helper 2>/dev/null || true
if [ -f /secrets/gitcreds/credentials ]; then
  cp /secrets/gitcreds/credentials "$HOME/.git-credentials" && chmod 600 "$HOME/.git-credentials"
  git config --global credential.helper store
fi
if [ -f /secrets/creds/gitlab_token ]; then
  git config --global --add credential.helper '!f() { echo "username=oauth2"; echo "password=$(cat /secrets/creds/gitlab_token)"; }; f'
fi

export AGENTHUB_STATE_RESTORED=0
if [ "${AGENTHUB_RESUME:-0}" = "1" ] && [ -n "${AGENTHUB_STATE_GET_URL:-}" ]; then
  echo "[entrypoint] Downloading session state from S3 …"
  [ "${AGENTHUB_S3_INSECURE:-0}" = "1" ] && CURL_K="-k" || CURL_K=""
  if curl -fsS $CURL_K -o /tmp/state.tgz "$AGENTHUB_STATE_GET_URL" &&
     tar xzf /tmp/state.tgz -C "$HOME" 2>/dev/null; then
    touch /tmp/.state-restored
    export AGENTHUB_STATE_RESTORED=1
    echo "[entrypoint] State restored."
  else
    echo "[entrypoint] WARN: no saved state – starting fresh without history."
  fi
fi

if [ "${AGENTHUB_HAS_MCP:-0}" = "1" ] && [ -f /secrets/mcp/mcp.json ]; then
  TARGET="${AGENTHUB_WORKDIR:-/workspace}"
  [ -d "$TARGET" ] || TARGET="/workspace"
  cp /secrets/mcp/mcp.json "$TARGET/.mcp.json" || true
fi
