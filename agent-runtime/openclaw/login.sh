#!/usr/bin/env bash
set -euo pipefail

# OPENCLAW_AUTH_FILE is set by entrypoint (AgentHub-managed auth.json under ~/.openclaw).
# Interactive login writes OpenClaw's real store (auth-profiles / sqlite); the AgentHub
# Secret sync still uses the PLACEHOLDER auth.json shape until export is wired.
if [ ! -f "${OPENCLAW_AUTH_FILE:-}" ]; then
  openclaw models auth add
fi
exec openclaw "$@"
