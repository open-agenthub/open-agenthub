#!/usr/bin/env bash
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "$0")" && pwd)"
source "$SCRIPT_DIR/apply-default-model.sh"

# OPENCLAW_AUTH_FILE is the AgentHub-managed auth-profiles.json under ~/.openclaw.
# Interactive login writes OpenClaw's SQLite store; export syncs store_json into that JSON.
if [ ! -f "${OPENCLAW_AUTH_FILE:-}" ]; then
  openclaw models auth add
  node "$SCRIPT_DIR/sync-auth-profiles.js" export
  # The login just decided which provider this session talks to, and OpenClaw's default model is
  # openai/gpt-5.5 regardless. Authenticating against Anthropic and then being told "auth or
  # provider access failed for openai" on the first message is what this prevents; the /auth flow
  # the TUI offers in response cannot fix it, because the credential was never the problem.
  apply_default_model || true
fi
exec openclaw "$@"
