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
    // Tool-permission notifications are handled by the PreToolUse flow — skip.
    if (/permission/i.test(p.message || "")) return;
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
