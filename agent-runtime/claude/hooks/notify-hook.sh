#!/usr/bin/env bash
payload="$(cat)"

body="$(printf '%s' "$payload" | node -e '
  let d = "";
  process.stdin.on("data", c => d += c).on("end", () => {
    let p = {}; try { p = JSON.parse(d); } catch {}
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
    process.stdout.write(JSON.stringify({ message: msg.slice(0, 1500), event: "question" }));
  });
')"

[ -z "$body" ] && body='{"message":"The agent is waiting for your reply.","event":"question"}'

curl -fsS -X POST "${AGENTHUB_CALLBACK_URL}/notify" \
  -H "X-Agent-Token: ${AGENTHUB_CALLBACK_TOKEN}" \
  -H "Content-Type: application/json" \
  -d "$body" >/dev/null 2>&1 || true
