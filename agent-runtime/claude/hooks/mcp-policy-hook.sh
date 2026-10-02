#!/usr/bin/env bash
set -u

NODE_BIN="${AGENTHUB_NODE_BIN:-node}"
CURL_BIN="${AGENTHUB_CURL_BIN:-curl}"
if [ "$NODE_BIN" = "node" ] && [ -x "/mnt/c/Program Files/nodejs/node.exe" ]; then NODE_BIN="/mnt/c/Program Files/nodejs/node.exe"; fi
if [ "$CURL_BIN" = "curl" ] && [ -x "/mnt/c/WINDOWS/system32/curl.exe" ]; then CURL_BIN="/mnt/c/WINDOWS/system32/curl.exe"; fi
SCRIPT_DIR="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"
APPROVAL_HOOK="${AGENTHUB_APPROVAL_HOOK:-$SCRIPT_DIR/pretooluse-hook.sh}"

render_settings() {
  # The skill reminder needs the runtime root, not the hooks directory AGENTHUB_RUNTIME
  # points at here; common/ sits one level above it.
  # Quoted: the local fallback interpreter above resolves to a path with a space in it,
  # and the hook command is handed to a shell.
  SKILL_REMINDER="'$NODE_BIN' '${RUNTIME:-/opt/session-agent}/common/skill-reminder-hook.mjs'"
  # Every mode gets the same PreToolUse pair. Unattended modes used to register only the
  # MCP matcher, which left auto-approve unreachable for them: the session flag is read in
  # the backend's /permission endpoint, and that endpoint is called by nothing but
  # pretooluse-hook.sh. An autonomous session therefore ran under --permission-mode
  # acceptEdits alone and stalled on the first Bash, WebFetch or WebSearch call with
  # "requires approval" — no prompt reaching anyone, no auto-approve answering it.
  #
  # The MCP timeout is 1900 for the same reason as the interactive one: this script
  # delegates to the approval hook, whose poll window (AGENTHUB_PERMISSION_POLL_SECONDS,
  # default 1740s) has to fit inside it. With auto-approve on, /permission answers "allow"
  # on the first POST and nothing polls at all.
  cat <<JSON
{
  "hooks": {
    "Notification": [
      { "hooks": [ { "type": "command", "command": "${AGENTHUB_RUNTIME:-/opt/session-agent/claude/hooks}/notify-hook.sh" } ] }
    ],
    "PreToolUse": [
      { "matcher": "mcp__.*", "hooks": [ { "type": "command", "command": "${AGENTHUB_RUNTIME:-/opt/session-agent/claude/hooks}/mcp-policy-hook.sh", "timeout": 1900 } ] },
      { "matcher": "^(?!mcp__).*", "hooks": [ { "type": "command", "command": "${AGENTHUB_RUNTIME:-/opt/session-agent/claude/hooks}/pretooluse-hook.sh", "timeout": 1900 } ] }
    ],
    "PostToolUse": [
      { "matcher": "^(Edit|Write|MultiEdit|NotebookEdit|Bash)$", "hooks": [ { "type": "command", "command": "$SKILL_REMINDER --mark work", "timeout": 10 } ] },
      { "matcher": ".*upload_skill$", "hooks": [ { "type": "command", "command": "$SKILL_REMINDER --mark uploaded", "timeout": 10 } ] }
    ],
    "Stop": [
      { "hooks": [ { "type": "command", "command": "$SKILL_REMINDER", "timeout": 10 } ] }
    ]
  }
}
JSON
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
  # The sharing policy said nothing against this call, so the ordinary approval decides it
  # — in every mode. Returning "{}" instead (as unattended modes did) hands the call back
  # to the CLI's own permission flow, which in a -p run with no terminal is a refusal that
  # auto-approve never gets the chance to overrule.
  printf '%s' "$payload" | "$APPROVAL_HOOK"
  exit $?
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
