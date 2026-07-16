#!/usr/bin/env bash
set -u

NODE_BIN="${AGENTHUB_NODE_BIN:-node}"
CURL_BIN="${AGENTHUB_CURL_BIN:-curl}"
if [ "$NODE_BIN" = "node" ] && [ -x "/mnt/c/Program Files/nodejs/node.exe" ]; then NODE_BIN="/mnt/c/Program Files/nodejs/node.exe"; fi
if [ "$CURL_BIN" = "curl" ] && [ -x "/mnt/c/WINDOWS/system32/curl.exe" ]; then CURL_BIN="/mnt/c/WINDOWS/system32/curl.exe"; fi
# Claude Code PreToolUse hook for the session-wide MCP sharing policy.
payload="$(cat)"

emit_deny() {
  printf '%s\n' '{"hookSpecificOutput":{"hookEventName":"PreToolUse","permissionDecision":"deny","permissionDecisionReason":"Blocked by the session MCP sharing policy"}}'
  exit 0
}

emit_allow() {
  printf '{}\n'
  exit 0
}

tool="$(printf '%s' "$payload" | "$NODE_BIN" -e '
  let data = "";
  process.stdin.on("data", chunk => data += chunk).on("end", () => {
    let payload = {};
    try { payload = JSON.parse(data); } catch {}
    process.stdout.write(typeof payload.tool_name === "string" ? payload.tool_name : "");
  });
')"

case "$tool" in
  mcp__*) ;;
  *) emit_allow ;;
esac

fail_closed() {
  if [ "${AGENTHUB_MCP_POLICY:-0}" = "1" ]; then
    emit_deny
  fi
  emit_allow
}

if [ -z "${AGENTHUB_CALLBACK_URL:-}" ]; then
  fail_closed
fi

request="$(printf '%s' "$tool" | "$NODE_BIN" -e '
  let tool = "";
  process.stdin.on("data", chunk => tool += chunk).on("end", () => {
    process.stdout.write(JSON.stringify({ tool }));
  });
')"

if ! response="$("$CURL_BIN" -fsS --connect-timeout 1 --max-time 3 \
  -X POST \
  -H "X-Agent-Token: ${AGENTHUB_CALLBACK_TOKEN:-}" \
  -H 'Content-Type: application/json' \
  --data "$request" \
  "${AGENTHUB_CALLBACK_URL}/mcp-policy" 2>/dev/null)"; then
  fail_closed
fi

if ! decision="$(printf '%s' "$response" | "$NODE_BIN" -e '
  let data = "";
  process.stdin.on("data", chunk => data += chunk).on("end", () => {
    let response;
    try { response = JSON.parse(data); } catch { process.exit(1); }
    if (!response || typeof response.restricted !== "boolean" ||
        !["allow", "deny"].includes(response.decision)) process.exit(1);
    process.stdout.write(response.restricted && response.decision === "deny" ? "deny" : "allow");
  });
')"; then
  fail_closed
fi

if [ "$decision" = "deny" ]; then
  emit_deny
fi
emit_allow
