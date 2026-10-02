#!/usr/bin/env bash
set -euo pipefail

RUNTIME=/opt/session-agent
if [ -d /opt/agenthub/session-agent ]; then
  RUNTIME=/opt/agenthub/session-agent
  export PATH="/opt/agenthub/bin:$PATH"
fi
export RUNTIME
export AGENTHUB_FILES_MCP_ENABLED=1
# The local skills proxy in front of the hub's skill-library server, so uploads and
# downloads can name a path instead of quoting file content into the agent's context.
# It only takes over an entry the hub actually injected; set to 0 to keep the plain
# HTTP entry instead.
export AGENTHUB_SKILLS_MCP_ENABLED="${AGENTHUB_SKILLS_MCP_ENABLED:-1}"

: "${AGENTHUB_STATE_DIR:?AGENTHUB_STATE_DIR is required}"
mkdir -p "$HOME/.ssh" "$HOME/$AGENTHUB_STATE_DIR"

# /tmp is an emptyDir, and Kubernetes chowns it to root:<fsGroup> with the setgid bit,
# which drops the sticky bit — it ends up world-writable without +t (0:1000, mode 2777).
# Tools that refuse such a directory then disable themselves (Claude Code turns off
# cross-session messaging, whose socket dir lives under the temp dir). The agent does not
# own /tmp and so cannot chmod it; point it at a private temp dir it does own instead.
export TMPDIR="$HOME/tmp"
mkdir -p "$TMPDIR"
chmod 700 "$TMPDIR"

[ -f /secrets/creds/git_user_name ]  && git config --global user.name  "$(cat /secrets/creds/git_user_name)"
[ -f /secrets/creds/git_user_email ] && git config --global user.email "$(cat /secrets/creds/git_user_email)"

if [ -f /secrets/creds/ssh_key ]; then
  cp /secrets/creds/ssh_key "$HOME/.ssh/id"; chmod 600 "$HOME/.ssh/id"
  export GIT_SSH_COMMAND="ssh -i $HOME/.ssh/id -o IdentitiesOnly=yes -o UserKnownHostsFile=/secrets/creds/known_hosts -o StrictHostKeyChecking=yes"
  git config --global core.sshCommand "$GIT_SSH_COMMAND"
fi

# One credential store holds both connected-provider OAuth tokens and manually stored
# PATs, every entry bound to its own host. There is deliberately no second, host-less
# credential helper: the one that used to serve a stored GitLab PAT was registered
# globally and answered with the user's token for any host that returned 401.
git config --global --unset-all credential.helper 2>/dev/null || true
if [ -f /secrets/gitcreds/credentials ]; then
  cp /secrets/gitcreds/credentials "$HOME/.git-credentials" && chmod 600 "$HOME/.git-credentials"
  git config --global credential.helper store
fi

# Derives gh/glab config from that same store, so a manual PAT authenticates the CLIs
# exactly like a connected provider does. That is what retired the GITLAB_TOKEN export
# this used to fall back to — which also keeps the token out of the session environment,
# where `env` and every subprocess could read it.
"$RUNTIME/common/setup-cli-auth.sh" || echo "[entrypoint] WARN: gh/glab auth setup failed"

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

MCP_SOURCE=""
if [ "${AGENTHUB_HAS_MCP:-0}" = "1" ] && [ -f /secrets/mcp/mcp.json ]; then
  MCP_SOURCE=/secrets/mcp/mcp.json
fi

MERGED_MCP=0
# A builtin is only merged when this image actually ships it. The runtimes do not all carry all of
# them — the OpenClaw image has files/ and network/ but neither browser/ nor sessions/ — while the
# enabling flags are instance-wide and not gated per agent. An unconditional `node` on a missing
# module exits non-zero, and under `set -e` in a sourced script that aborts the entrypoint: on an
# instance with the browser or the spawn MCP switched on, an OpenClaw session died with
# MODULE_NOT_FOUND before the agent ever started.
merge_builtin_mcp() {
  module="$RUNTIME/$1/$2"
  if [ ! -f "$module" ]; then
    echo "[entrypoint] builtin MCP '$1' is enabled but not shipped in this image; skipping."
    return 0
  fi
  node "$module" "$MCP_SOURCE"
  MCP_SOURCE=/tmp/agenthub-mcp.json
  MERGED_MCP=1
}

if [ "${AGENTHUB_BROWSER_ENABLED:-0}" = "1" ]; then
  merge_builtin_mcp browser configure-claude.mjs
fi
if [ "${AGENTHUB_SPAWN_MCP_ENABLED:-0}" = "1" ]; then
  merge_builtin_mcp sessions configure.mjs
fi
if [ "${AGENTHUB_NETWORK_MCP_ENABLED:-0}" = "1" ]; then
  merge_builtin_mcp network configure.mjs
fi
if [ "${AGENTHUB_FILES_MCP_ENABLED:-0}" = "1" ]; then
  merge_builtin_mcp files configure.mjs
fi
# Only ever rewrites an existing skill-library entry, so there is nothing to do without a
# config — and writing one here would hand the agent an empty .mcp.json it never had. Goes
# through merge_builtin_mcp for the same reason the others do: an image that does not ship
# skills/ must skip the step, not abort the entrypoint on MODULE_NOT_FOUND.
if [ "${AGENTHUB_SKILLS_MCP_ENABLED:-0}" = "1" ] && [ -n "$MCP_SOURCE" ]; then
  merge_builtin_mcp skills configure.mjs
fi
if [ "$MERGED_MCP" = "1" ]; then
  export AGENTHUB_MCP_CONFIG=/tmp/agenthub-mcp.json
elif [ -n "$MCP_SOURCE" ]; then
  export AGENTHUB_MCP_CONFIG="$MCP_SOURCE"
fi
# AGENTHUB_MCP_CONFIG stays outside the workspace on purpose. This used to be copied to
# $AGENTHUB_WORKDIR/.mcp.json, which with a single repository is the clone itself — an
# untracked file in a tree the agent is about to commit. It did not even work: measured
# against Claude Code 2.1.283, a project .mcp.json server reports "⏸ Pending approval
# (run `claude` to approve)" and is never connected to, so an unattended session got
# nothing from it. Codex and Cursor ignore the file outright — each reads its own config,
# which its entrypoint writes under $HOME (verified with `codex mcp list` and
# `cursor-agent mcp list`, the latter naming its locations in the error itself).
# Claude's central equivalent is written by claude/mcp-config.mjs.
