#!/usr/bin/env bash
# #279 end to end: real autonomous Codex sessions (the branch's common/server.js + codex driver +
# pinned `codex exec`) and a real Claude Code turn report into the REAL controllers and stores on
# real Postgres. Needs: proof host (host/) on $HUB with a fresh database, mock-model.mjs on $MODEL.
# usage: prove-279.sh <worktree> <scratch-dir> <codex-bin-dir> [hub] [model]
set -eu
WT=$1; S=$2; BIN=$3; HUB=${4:-http://127.0.0.1:8080}; MODEL=${5:-http://127.0.0.1:18780}; R=$(dirname "$0")
seed() { # id owner title agent(0 Claude,1 Codex) authMode(1 Subscription,2 ApiKey)
  curl -fsS -X POST "$HUB/__seed" -H 'content-type: application/json' \
    -d "{\"id\":\"$1\",\"owner\":\"$2\",\"title\":\"$3\",\"agent\":$4,\"authMode\":$5,\"mode\":1,\"token\":\"tok-$1\"}" >/dev/null
}
seed claude-sub alice "Claude on a subscription" 0 1
seed codex-sub  alice "Codex on a subscription"  1 1
seed codex-api  alice "Codex on an API key"      1 2
seed codex-bob  bob   "Bob's Codex"              1 1
"$R/run-claude.sh" "$S/c-sub" claude-sub alice - subscription "$HUB" "$MODEL" >/dev/null
for sid in codex-sub codex-api codex-bob; do "$R/run-codex-agent.sh" "$WT" "$S/h-$sid" "$sid" "$HUB" "$MODEL" "$BIN" >/dev/null 2>&1; done
# The same session started again on its kept CODEX_HOME (what a resume restores): the agent reports
# the new cumulative totals over both rollouts, and the hub adds only the difference.
"$R/run-codex-agent.sh" "$WT" "$S/h-codex-sub" codex-sub "$HUB" "$MODEL" "$BIN" >/dev/null 2>&1
show() {
  curl -fsS -X POST "$HUB/__as/$1" >/dev/null
  echo "== $1 rows"
  curl -fsS "$HUB/api/usage/sessions" | node -e 'for (const r of JSON.parse(require("fs").readFileSync(0)).sort((a,b)=>a.sessionId.localeCompare(b.sessionId))) console.log(r.sessionId.padEnd(11), (r.billing||"").padEnd(13), (r.source||"").padEnd(12), "in",r.inputTokens,"out",r.outputTokens,"cacheRead",r.cacheReadTokens,"reasoning",r.reasoningTokens,"turns",r.turns,"api$",r.apiCostUsd,"costReported",r.costReported)'
}
show alice
echo "== alice summary"; curl -fsS "$HUB/api/usage/summary" | node -e 'const s=JSON.parse(require("fs").readFileSync(0)); console.log("apiCostUsd",s.apiCostUsd,"subscriptionTokens",s.subscriptionTokens,"subscriptionTurns",s.subscriptionTurns)'
echo "== alice reading bob's session: $(curl -s -o /dev/null -w '%{http_code}' "$HUB/api/usage/sessions/codex-bob")"
show bob
echo "== report with another session's token: $(curl -s -o /dev/null -w '%{http_code}' -X POST "$HUB/internal/sessions/codex-sub/usage" \
  -H 'content-type: application/json' -H 'X-Agent-Token: tok-codex-bob' \
  -d '{"inputTokens":999999,"outputTokens":0,"cacheReadTokens":0,"cacheCreationTokens":0,"reasoningTokens":0,"turns":0,"costUsd":null}')"
curl -fsS -X POST "$HUB/__as/alice" >/dev/null
