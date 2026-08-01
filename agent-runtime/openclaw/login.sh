#!/usr/bin/env bash
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "$0")" && pwd)"

# OPENCLAW_AUTH_FILE is the AgentHub-managed auth-profiles.json under ~/.openclaw.
# Interactive login writes OpenClaw's SQLite store; export syncs store_json into that JSON.
if [ ! -f "${OPENCLAW_AUTH_FILE:-}" ]; then
  openclaw models auth add
  node "$SCRIPT_DIR/sync-auth-profiles.js" export
fi
exec openclaw "$@"
