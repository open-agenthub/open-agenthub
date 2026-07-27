#!/usr/bin/env bash
set -euo pipefail

# CURSOR_AUTH_FILE is set by entrypoint (canonical auth.json under CURSOR_CONFIG_DIR).
if [ ! -f "${CURSOR_AUTH_FILE:-}" ]; then
  agent login
fi
exec agent "$@"
