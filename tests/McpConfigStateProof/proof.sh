#!/usr/bin/env bash
# #446 proof for OpenClaw, run by run-local.sh inside proot. Usage: proof.sh <checkout>
set -uo pipefail
SRC="$(cd "${1:?checkout}" && pwd)"
PROOF="$SRC/tests/McpConfigStateProof"
TOKEN=proof-x-agent-token-446-0123456789
SID=sess-446
export EXPECTED_TOKEN="$TOKEN" STATE_FILE=/tmp/state.tgz

RT=/opt/agenthub/session-agent
mkdir -p "$RT" /opt/agenthub/bin
grep -E '^COPY [a-z-]+(/[a-z.-]+)? /opt/session-agent/' "$SRC/agent-runtime/openclaw/Dockerfile" \
  | while read -r _ from to; do
      dest="$RT/${to#/opt/session-agent/}"; mkdir -p "$(dirname "$dest")"; cp -r "$SRC/agent-runtime/$from" "$dest"
    done
# The pod's own runtime: same base and node-pty build as the image.
[ -e "$RT/node_modules" ] || ln -s /opt/session-agent/node_modules "$RT/node_modules"
chmod +x "$RT"/common/*.sh "$RT"/openclaw/*.sh
cp "$PROOF/openclaw-wrapper.sh" /opt/agenthub/bin/openclaw

node "$PROOF/hub.mjs" > /tmp/hub.log 2>&1 &
HUB_PID=$!
trap 'kill $HUB_PID 2>/dev/null' EXIT
sleep 1

# The hub's skill-library entry exactly as McpConfigAssembler injects it. With the local proxy
# switched off it reaches OpenClaw as an HTTP server whose X-Agent-Token header is a literal value.
mkdir -p /secrets/mcp
cat > /secrets/mcp/mcp.json <<JSON
{"mcpServers":{"skill-library":{"type":"http","url":"http://127.0.0.1:18093/internal/sessions/$SID/mcp","headers":{"X-Agent-Token":"$TOKEN"}}}}
JSON

start() {
  env -i PATH=/usr/local/sbin:/usr/local/bin:/usr/sbin:/usr/bin:/sbin:/bin HOME=/home/agent \
    TERMINATION_MESSAGE_PATH=/tmp/termination-log AGENTHUB_PORT="${PROOF_AGENT_PORT:-7681}" \
    AGENTHUB_SESSION_ID="$SID" AGENTHUB_MODE=autonomous AGENTHUB_AUTO_APPROVE=1 AGENTHUB_PROMPT=x \
    AGENTHUB_AUTH_MODE=apikey OPENAI_API_KEY=sk-proof-not-a-real-key AGENTHUB_SKILLS_MCP_ENABLED=0 \
    AGENTHUB_CALLBACK_URL="http://127.0.0.1:18093/internal/sessions/$SID" AGENTHUB_CALLBACK_TOKEN="$TOKEN" \
    AGENTHUB_WORKDIR="${PROOF_WORKDIR:-/workspace}" AGENTHUB_HAS_MCP=1 "$@" \
    timeout --kill-after=10 300 bash "$RT/openclaw/entrypoint.sh" > "/tmp/entrypoint-$RUN.log" 2>&1
  echo "entrypoint exit: $?"
}

archive() {
  ( cd /home/agent && node -e '
const { archiveScript } = require(process.argv[1] + "/common/state-archive");
const driver = require(process.argv[1] + "/openclaw/driver");
require("child_process").execFileSync("/bin/sh", ["-c", archiveScript(driver), "sh", "/tmp/state.tgz"]);' "$RT" )
  echo "token occurrences in the state archive: $(gunzip -c /tmp/state.tgz | grep -a -c "$TOKEN")"
}

if [ -n "${LEGACY_STATE:-}" ]; then
  # An archive written by an older runtime: resume from it, then archive again.
  cp "$LEGACY_STATE" /tmp/state.tgz
  echo "===== LEGACY: token occurrences in the archive being restored: $(gunzip -c /tmp/state.tgz | grep -a -c "$TOKEN")"
else
echo "===== RUN 1: fresh session"
RUN=1; start
cat /tmp/openclaw-wrapper.log; : > /tmp/openclaw-wrapper.log
echo "skill-library entry in openclaw.json:"
node -e 'const c=require("/home/agent/.openclaw/openclaw.json"); console.log(JSON.stringify(c.mcp.servers["skill-library"]))'
echo "token occurrences in openclaw.json: $(grep -c "$TOKEN" /home/agent/.openclaw/openclaw.json)"
echo "===== the agent adds a server of its own, as a person in the terminal would"
OPENCLAW_STATE_DIR=/home/agent/.openclaw OPENCLAW_CONFIG_PATH=/home/agent/.openclaw/openclaw.json HOME=/home/agent \
  OPENCLAW_NO_AUTO_UPDATE=1 /usr/local/bin/openclaw mcp set agent_added '{"command":"node","args":["-e","0"]}' 2>&1 | tail -2
archive
fi

echo "===== RUN 2: resumed from that archive in an empty home"
rm -rf /home/agent/* /home/agent/.[!.]* /tmp/agenthub-mcp.json /tmp/agenthub-openclaw-mcp.env /tmp/.state-restored
RUN=2; start AGENTHUB_RESUME=1 AGENTHUB_STATE_GET_URL=http://127.0.0.1:18093/state.tgz
grep -E "State restored|no saved state" /tmp/entrypoint-2.log
cat /tmp/openclaw-wrapper.log
echo "token occurrences in openclaw.json: $(grep -c "$TOKEN" /home/agent/.openclaw/openclaw.json)"
archive
echo "===== hub: skill-library requests (tokenOk is the header OpenClaw sent)"
grep '\[hub\] {' /tmp/hub.log | sort | uniq -c
echo "===== PROOF DONE"
