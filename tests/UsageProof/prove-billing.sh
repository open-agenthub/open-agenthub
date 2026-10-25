#!/usr/bin/env bash
# #277 end to end: real Claude Code 2.1.287 turns export OTLP into the REAL OtelController, the
# REAL PostgresUsageStore classifies them from hub-side state, and the REAL UsageController
# answers. Needs: proof host (host/) on $HUB with a fresh database, mock-model.mjs on $MODEL.
# usage: prove-billing.sh <scratch-dir> [hub] [model]
set -eu
S=$1; HUB=${2:-http://127.0.0.1:8080}; MODEL=${3:-http://127.0.0.1:18780}; R=$(dirname "$0")
seed() { # id title authMode(0 Auto,1 Subscription,2 ApiKey) mode(0 Interactive,1 Autonomous) [usageBilling]
  local b=null; [ -n "${5:-}" ] && b="\"$5\""
  curl -fsS -X POST "$HUB/__seed" -H 'content-type: application/json' \
    -d "{\"id\":\"$1\",\"owner\":\"alice\",\"title\":\"$2\",\"agent\":0,\"authMode\":$3,\"mode\":$4,\"token\":\"tok-$1\",\"usageBilling\":$b}" >/dev/null
}
seed claude-sub   "Claude, subscription"                1 1
seed claude-api   "Claude, API key"                     2 1
seed auto-cron    "Claude Auto, unattended, key+login"  0 1 api
seed auto-tui     "Claude Auto, interactive, key+login" 0 0 unknown
seed forged       "Claude, API key, forged payload"     2 1
"$R/run-claude.sh" "$S/c-sub"  claude-sub alice - subscription "$HUB" "$MODEL"
"$R/run-claude.sh" "$S/c-api"  claude-api alice - apikey       "$HUB" "$MODEL"
"$R/run-claude.sh" "$S/c-cron" auto-cron  alice - both         "$HUB" "$MODEL"
"$R/run-claude.sh" "$S/c-tui"  auto-tui   alice - subscription "$HUB" "$MODEL"
# The payload claims a subscription and another owner; the hub must ignore both.
"$R/run-claude.sh" "$S/c-forged" forged alice "agenthub.billing=subscription,user.id=mallory" apikey "$HUB" "$MODEL"
curl -fsS -X POST "$HUB/__as/alice" >/dev/null
echo "== rows"; curl -fsS "$HUB/api/usage/sessions" | node -e 'for (const r of JSON.parse(require("fs").readFileSync(0)).sort((a,b)=>a.sessionId.localeCompare(b.sessionId))) console.log(r.sessionId.padEnd(11), (r.billing||"").padEnd(13), "reported $"+r.costUsd, "api $"+r.apiCostUsd, "apiBilled="+r.apiBilled)'
echo "== summary"; curl -fsS "$HUB/api/usage/summary" | node -e 'const s=JSON.parse(require("fs").readFileSync(0)); console.log("apiCostUsd",s.apiCostUsd,"apiSessions",s.apiSessionCount,"subscriptionSessions",s.subscriptionSessionCount,"subscriptionTokens",s.subscriptionTokens)'
echo "== month-to-date budget"; curl -fsS "$HUB/api/usage/limit"; echo
curl -fsS -X POST "$HUB/__as/mallory" >/dev/null; echo "== mallory sees"; curl -fsS "$HUB/api/usage/sessions"; echo
curl -fsS -X POST "$HUB/__as/alice" >/dev/null
