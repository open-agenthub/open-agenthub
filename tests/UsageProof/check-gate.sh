#!/usr/bin/env bash
# The monthly-budget start gate on recorded spend: sets alice's personal limit below what her
# sessions already reported, then asks the real UsageLimitService (POST /__can-start) per agent and
# auth mode. Run after prove-opencode-openclaw.sh (or any run that recorded API spend for alice).
# usage: check-gate.sh [hub] [limit-usd]
set -eu
HUB=${1:-http://127.0.0.1:8080}; LIMIT=${2:-0.003}
curl -fsS -X POST "$HUB/__as/alice" >/dev/null
curl -fsS -X PUT "$HUB/api/usage/limit" -H 'content-type: application/json' -d "{\"limitUsd\":$LIMIT}" \
  | node -e 'const l=JSON.parse(require("fs").readFileSync(0)); console.log(`limit $${l.effectiveLimitUsd}, spent $${l.monthApiCostUsd}, blocked=${l.blocked}`)'
gate() { # label agent(0 Claude,1 Codex,2 Cursor,3 OpenClaw,4 OpenCode) authMode(1 Subscription,2 ApiKey,3 CustomEndpoint)
  local code; code=$(curl -s -o /dev/null -w '%{http_code}' -X POST "$HUB/__can-start" -H 'content-type: application/json' \
    -d "{\"owner\":\"alice\",\"agent\":$2,\"authMode\":$3,\"mode\":1}")
  printf '%-28s %s\n' "$1" "$([ "$code" = 200 ] && echo allowed || echo "refused ($code)")"
}
gate "Claude, API key" 0 2
gate "OpenCode, API key" 4 2
gate "OpenClaw, API key" 3 2
gate "OpenCode, subscription" 4 1
gate "OpenClaw, subscription" 3 1
gate "OpenCode, custom endpoint" 4 3
gate "Codex, API key" 1 2
gate "Cursor, API key" 2 2
