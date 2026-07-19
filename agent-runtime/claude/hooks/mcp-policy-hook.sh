#!/usr/bin/env bash
set -u

NODE_BIN="${AGENTHUB_NODE_BIN:-node}"
CURL_BIN="${AGENTHUB_CURL_BIN:-curl}"
if [ "$NODE_BIN" = "node" ] && [ -x "/mnt/c/Program Files/nodejs/node.exe" ]; then NODE_BIN="/mnt/c/Program Files/nodejs/node.exe"; fi
if [ "$CURL_BIN" = "curl" ] && [ -x "/mnt/c/WINDOWS/system32/curl.exe" ]; then CURL_BIN="/mnt/c/WINDOWS/system32/curl.exe"; fi
SCRIPT_DIR="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"
APPROVAL_HOOK="${AGENTHUB_APPROVAL_HOOK:-$SCRIPT_DIR/pretooluse-hook.sh}"

render_settings() {
  if [ "${AGENTHUB_MODE:-interactive}" = "interactive" ]; then
    cat <<JSON
{
  "hooks": {
    "Notification": [
      { "hooks": [ { "type": "command", "command": "${AGENTHUB_RUNTIME:-/opt/session-agent/claude/hooks}/notify-hook.sh" } ] }
    ],
    "PreToolUse": [
      { "matcher": "mcp__.*", "hooks": [ { "type": "command", "command": "${AGENTHUB_RUNTIME:-/opt/session-agent/claude/hooks}/mcp-policy-hook.sh", "timeout": 300 } ] },
      { "matcher": "^(?!mcp__).*", "hooks": [ { "type": "command", "command": "${AGENTHUB_RUNTIME:-/opt/session-agent/claude/hooks}/pretooluse-hook.sh", "timeout": 300 } ] }
    ]
  }
}
JSON
  else
    cat <<JSON
{
  "hooks": {
    "Notification": [
      { "hooks": [ { "type": "command", "command": "${AGENTHUB_RUNTIME:-/opt/session-agent/claude/hooks}/notify-hook.sh" } ] }
    ],
    "PreToolUse": [
      { "matcher": "mcp__.*", "hooks": [ { "type": "command", "command": "${AGENTHUB_RUNTIME:-/opt/session-agent/claude/hooks}/mcp-policy-hook.sh", "timeout": 5 } ] }
    ]
  }
}
JSON
  fi
}

if [ "${1:-}" = "--settings" ]; then
  render_settings
  exit 0
fi

payload="$(cat)"

emit_deny() {
  printf '%s\n' '{"hookSpecificOutput":{"hookEventName":"PreToolUse","permissionDecision":"deny","permissionDecisionReason":"Blocked by the session MCP sharing policy"}}'
  exit 0
}

continue_flow() {
  if [ "${AGENTHUB_MODE:-}" = "interactive" ]; then
    printf '%s' "$payload" | "$APPROVAL_HOOK"
    exit $?
  fi
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
  *) continue_flow ;;
esac

fail_closed() {
  if [ "${AGENTHUB_MCP_POLICY:-0}" = "1" ]; then
    emit_deny
  fi
  continue_flow
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
continue_flow
