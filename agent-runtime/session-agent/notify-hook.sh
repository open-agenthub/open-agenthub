#!/usr/bin/env bash
# Called by Claude Code on the Notification event (hook payload as JSON on stdin).
# Forwards the agent's actual last message (from the transcript) to the backend,
# which relays it to Slack — falling back to the generic notification text.
payload="$(cat)"

body="$(printf '%s' "$payload" | node -e '
  let d = "";
  process.stdin.on("data", c => d += c).on("end", () => {
    let p = {}; try { p = JSON.parse(d); } catch {}
    let msg = p.message || "The agent is waiting for your reply.";
    // Prefer the last assistant text turn from the transcript (the real question).
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
    process.stdout.write(JSON.stringify({ message: msg.slice(0, 12000), event: "question" }));
  });
')"

[ -z "$body" ] && body='{"message":"The agent is waiting for your reply.","event":"question"}'

curl -fsS -X POST "${AGENTHUB_CALLBACK_URL}/notify" \
  -H "X-Agent-Token: ${AGENTHUB_CALLBACK_TOKEN}" \
  -H "Content-Type: application/json" \
  -d "$body" >/dev/null 2>&1 || true
