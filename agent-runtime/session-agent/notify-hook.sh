#!/usr/bin/env bash
# Called by Claude Code on the Notification event (input via stdin as JSON).
payload="$(cat)"
msg="$(printf '%s' "$payload" | sed -n 's/.*"message"[[:space:]]*:[[:space:]]*"\([^"]*\)".*/\1/p')"
[ -z "$msg" ] && msg="The agent is waiting for your reply."
curl -fsS -X POST "${AGENTHUB_CALLBACK_URL}/notify" \
  -H "X-Agent-Token: ${AGENTHUB_CALLBACK_TOKEN}" \
  -H "Content-Type: application/json" \
  -d "{\"message\":\"$(printf '%s' "$msg" | sed 's/\\/\\\\/g; s/"/\\"/g')\",\"event\":\"question\"}" >/dev/null 2>&1 || true
