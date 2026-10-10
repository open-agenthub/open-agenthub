#!/usr/bin/env bash
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "$0")" && pwd)"

# Run once after the hub swapped this session's login (driver.installCredential): the default
# model in the config follows the login, and the new one may be for another provider. A failure
# keeps the old config rather than the session from starting.
node "$SCRIPT_DIR/user-config.js" "${AGENTHUB_OPENCODE_CONFIG:-$HOME/.config/opencode/opencode.json}" \
  || echo "[opencode] could not refresh the session config after the account switch." >&2
exec opencode "$@"
