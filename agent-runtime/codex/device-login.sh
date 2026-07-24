#!/usr/bin/env bash
set -euo pipefail

if [ ! -f "$CODEX_HOME/auth.json" ]; then
  codex login --device-auth
fi
exec codex "$@"
