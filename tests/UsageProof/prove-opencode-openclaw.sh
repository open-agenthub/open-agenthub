#!/usr/bin/env bash
# OpenCode and OpenClaw end to end (#282): real autonomous sessions through the branch's
# common/server.js and each runtime's real driver report into the REAL controllers and stores on
# real Postgres; then a long Codex turn shows usage reported on the 30-second tick while it runs.
# Needs: proof host (host/) on $HUB with a fresh database, mock-model.mjs on $MODEL.
# usage: prove-opencode-openclaw.sh <worktree> <scratch-dir> <cli-bin-dir> [hub] [model]
set -eu
WT=$1; S=$2; BIN=$3; HUB=${4:-http://127.0.0.1:8080}; MODEL=${5:-http://127.0.0.1:18780}; R=$(dirname "$0")
seed() { # id title agent(1 Codex,3 OpenClaw,4 OpenCode) authMode(1 Subscription,2 ApiKey)
  curl -fsS -X POST "$HUB/__seed" -H 'content-type: application/json' \
    -d "{\"id\":\"$1\",\"owner\":\"alice\",\"title\":\"$2\",\"agent\":$3,\"authMode\":$4,\"mode\":1,\"token\":\"tok-$1\"}" >/dev/null
}
seed opencode-api "OpenCode on an API key" 4 2
seed openclaw-api "OpenClaw on an API key" 3 2
seed opencode-sub "OpenCode on a subscription" 4 1
seed codex-tick   "Codex, long turn" 1 1
for run in "opencode opencode-api" "openclaw openclaw-api" "opencode opencode-sub"; do
  set -- $run; "$R/run-agent.sh" "$1" "$WT" "$S/h-$2" "$2" "$HUB" "$MODEL" "$BIN" >/dev/null 2>&1
done
curl -fsS -X POST "$HUB/__as/alice" >/dev/null
echo "== rows"
curl -fsS "$HUB/api/usage/sessions" | node -e 'for (const r of JSON.parse(require("fs").readFileSync(0)).sort((a,b)=>a.sessionId.localeCompare(b.sessionId))) console.log(r.sessionId.padEnd(13), (r.billing||"").padEnd(13), (r.source||"").padEnd(9), "in",r.inputTokens,"out",r.outputTokens,"cacheRead",r.cacheReadTokens,"reasoning",r.reasoningTokens,"turns",r.turns,"reported $"+r.costUsd,"api $"+r.apiCostUsd)'
echo "== long Codex turn (TICK: the answer comes 40 s after the first response)"
"$R/run-agent.sh" codex "$WT" "$S/h-codex-tick" codex-tick "$HUB" "$MODEL" "$BIN" "TICK please" >/dev/null 2>&1 &
agent=$!; t0=$(date +%s)
while kill -0 $agent 2>/dev/null; do
  sleep 5
  row=$(curl -s "$HUB/api/usage/sessions/codex-tick" | node -e 'let d="";process.stdin.on("data",c=>d+=c).on("end",()=>{try{const r=JSON.parse(d);console.log(r.inputTokens === undefined ? "nothing yet" : "input "+r.inputTokens)}catch{console.log("nothing yet")}})')
  running=$(kill -0 $agent 2>/dev/null && echo running || echo exited)
  echo "t+$(( $(date +%s) - t0 ))s ($running): $row"
done
