#!/usr/bin/env bash
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "$0")" && pwd)"

# OPENCODE_AUTH_FILE is the canonical auth.json the entrypoint links OpenCode's data dir to.
# `opencode auth login` lists the providers; OpenCode Go takes the API key from the opencode.ai
# console. The watcher picks the file up and stores it for the next session.
if [ ! -f "${OPENCODE_AUTH_FILE:-}" ]; then
  opencode auth login || true
  # The login just decided which provider this session talks to; regenerate the config so the
  # default model follows it instead of whatever OpenCode would pick across all providers.
  node "$SCRIPT_DIR/user-config.js" "${AGENTHUB_OPENCODE_CONFIG:-$HOME/.config/opencode/opencode.json}" \
    || echo "[opencode] could not refresh the session config after login." >&2
fi
exec opencode "$@"
