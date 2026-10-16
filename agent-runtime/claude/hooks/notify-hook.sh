#!/usr/bin/env bash
# Claude Code Notification hook. Forwards ONLY genuine "the agent is waiting /
# asking" notifications to the backend. Permission notifications ("Claude needs
# your permission to use X") are dropped: in interactive mode the PreToolUse
# flow already prompts the user, and in autonomous mode nobody should be asked
# to approve tools at all — only real questions may reach the messengers.
payload="$(cat)"

NODE_BIN="${AGENTHUB_NODE_BIN:-node}"
if [ "$NODE_BIN" = "node" ] && [ -x "/mnt/c/Program Files/nodejs/node.exe" ]; then NODE_BIN="/mnt/c/Program Files/nodejs/node.exe"; fi
CURL_BIN="${AGENTHUB_CURL_BIN:-curl}"
if [ "$CURL_BIN" = "curl" ] && [ -x "/mnt/c/WINDOWS/system32/curl.exe" ]; then CURL_BIN="/mnt/c/WINDOWS/system32/curl.exe"; fi

body="$(printf '%s' "$payload" | "$NODE_BIN" -e '
  let d = "";
  process.stdin.on("data", c => d += c).on("end", () => {
    let p = {}; try { p = JSON.parse(d); } catch {}
    // The CLI names the kind of notification (2.1.x: permission_prompt, idle_prompt,
    // auth_success, elicitation_dialog, …). Only the permission prompt is handled
    // elsewhere; a completed login is not a question either. Everything else is the
    // agent waiting on a person. Matching the message text instead used to drop any
    // assistant question that happened to contain the word "permission".
    const kind = typeof p.notification_type === "string" ? p.notification_type : "";
    if (kind === "permission_prompt" || kind === "auth_success") return;
    // Older CLIs sent no notification_type; their permission prompt is the one fixed phrase.
    if (!kind && /^Claude needs your permission\b/.test(p.message || "")) return;
    let msg = p.message || "The agent is waiting for your reply.";
    try {
      const fs = require("fs");
      const lines = fs.readFileSync(p.transcript_path, "utf8").trim().split("\n");
      for (let i = lines.length - 1; i >= 0; i--) {
        let e; try { e = JSON.parse(lines[i]); } catch { continue; }
        if (e.type === "assistant" && e.message && Array.isArray(e.message.content)) {
          const t = e.message.content.filter(x => x.type === "text").map(x => x.text).join("\n").trim();
          if (t) { msg = t; break; }
        }
      }
    } catch {}
    process.stdout.write(JSON.stringify({ message: msg.slice(0, 12000).replace(/[\uD800-\uDBFF]$/, ""), event: "question" }));
  });
')"

# Empty body = filtered out (permission notification) — nothing to forward.
[ -z "$body" ] && exit 0

"$CURL_BIN" -fsS -X POST "${AGENTHUB_CALLBACK_URL}/notify" \
  -H "X-Agent-Token: ${AGENTHUB_CALLBACK_TOKEN}" \
  -H "Content-Type: application/json" \
  -d "$body" >/dev/null 2>&1 || true
