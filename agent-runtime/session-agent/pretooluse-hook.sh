#!/usr/bin/env bash
# Claude Code PreToolUse hook. Asks the backend whether the tool may run; the backend
# may prompt the user via Slack (interactive buttons). Falls back to the normal
# permission flow ("ask") when there is no out-of-band approver, or after the poll
# window (default 29 min) expires — in which case the backend defuses the chat prompt.
payload="$(cat)"

emit() {
  case "$1" in allow|allowAlways) pd=allow ;; deny) pd=deny ;; *) pd=ask ;; esac
  printf '{"hookSpecificOutput":{"hookEventName":"PreToolUse","permissionDecision":"%s","permissionDecisionReason":"Open AgentHub"}}\n' "$pd"
  exit 0
}
field() { # extract a top-level string field from JSON on stdin
  node -e 'let d="";process.stdin.on("data",c=>d+=c).on("end",()=>{let p={};try{p=JSON.parse(d)}catch{};process.stdout.write((p["'"$1"'"]||"")+"")})'
}

[ -z "${AGENTHUB_CALLBACK_URL:-}" ] && emit ask

req="$(printf '%s' "$payload" | node -e '
  let d="";process.stdin.on("data",c=>d+=c).on("end",()=>{
    let p={}; try{p=JSON.parse(d)}catch{}
    let s=""; try{ s = typeof p.tool_input==="string" ? p.tool_input : JSON.stringify(p.tool_input); }catch{}
    process.stdout.write(JSON.stringify({ tool: p.tool_name||"a tool", input: (s||"").slice(0,800) }));
  });')"

resp="$(curl -fsS --connect-timeout 2 --max-time 5 -X POST -H "X-Agent-Token: $AGENTHUB_CALLBACK_TOKEN" \
  -H "Content-Type: application/json" -d "$req" "$AGENTHUB_CALLBACK_URL/permission" 2>/dev/null)"
[ -z "$resp" ] && emit ask

dec="$(printf '%s' "$resp" | field decision)"
[ -n "$dec" ] && [ "$dec" != "pending" ] && emit "$dec"
id="$(printf '%s' "$resp" | field id)"
[ -z "$id" ] && emit ask

# Poll for the decision on the wall clock (bash $SECONDS counts since script start, so
# curl/node spawn time is included). Configurable window, default 29 min — a margin
# below the 1900s hook 'timeout' in settings.json, which is the hard cap.
poll="${AGENTHUB_PERMISSION_POLL_SECONDS:-1740}"
case "$poll" in ''|*[!0-9]*) poll=1740;; esac
while [ "$SECONDS" -lt "$poll" ]; do
  if [ "$SECONDS" -lt 60 ]; then sleep 2; else sleep 5; fi
  dec="$(curl -fsS --connect-timeout 2 --max-time 5 -H "X-Agent-Token: $AGENTHUB_CALLBACK_TOKEN" \
    "$AGENTHUB_CALLBACK_URL/permission/$id" 2>/dev/null | field decision)"
  [ -n "$dec" ] && [ "$dec" != "pending" ] && emit "$dec"
done
# Gave up: tell the backend so it defuses the chat buttons. It answers with the final
# decision — if a click won the race against this expire, honor it ("expired" → ask).
dec="$(curl -fsS --connect-timeout 2 --max-time 5 -X POST -H "X-Agent-Token: $AGENTHUB_CALLBACK_TOKEN" \
  "$AGENTHUB_CALLBACK_URL/permission/$id/expire" 2>/dev/null | field decision)"
[ -n "$dec" ] && emit "$dec"
emit ask
