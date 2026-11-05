#!/usr/bin/env bash
# Runs inside the released OpenClaw runtime image (see README.md). Usage: proof.sh <git ref>
set -uo pipefail
REF="${1:?git ref}"
SRC=/tmp/src
mkdir -p "$SRC"
curl -fsSL "https://codeload.github.com/open-agenthub/open-agenthub/tar.gz/$REF" | tar xz -C "$SRC" --strip-components=1
PROOF="$SRC/tests/OpenClawBuiltinMcpProof"
TOKEN=proof-callback-token-0001
SID=sess-openclaw-proof
export EXPECTED_TOKEN="$TOKEN"

echo "===== image: openclaw $(openclaw --version 2>/dev/null | head -1)"
echo "===== BEFORE: builtins shipped in the released image"
ls /opt/session-agent

echo "===== BEFORE: released mcp-config.js with every builtin flag on"
BEFORE_CFG=/tmp/before/openclaw.json
mkdir -p /tmp/before
env RUNTIME=/opt/session-agent AGENTHUB_BROWSER_ENABLED=1 AGENTHUB_SPAWN_MCP_ENABLED=1 \
  AGENTHUB_FILES_MCP_ENABLED=1 AGENTHUB_NETWORK_MCP_ENABLED=1 \
  AGENTHUB_CALLBACK_URL="http://127.0.0.1:18092/internal/sessions/$SID" AGENTHUB_CALLBACK_TOKEN="$TOKEN" \
  node /opt/session-agent/openclaw/mcp-config.js "$BEFORE_CFG" ""
env HOME=/tmp/before OPENCLAW_STATE_DIR=/tmp/before OPENCLAW_CONFIG_PATH="$BEFORE_CFG" \
  OPENCLAW_NO_AUTO_UPDATE=1 openclaw mcp list 2>&1

echo "===== AFTER: the branch's runtime, laid out as its Dockerfile COPY lines say"
# /opt/agenthub/session-agent is where the platform injects the runtime into a custom image;
# entrypoint-common.sh switches RUNTIME to it when it exists. The image's own node_modules are
# reused, so only the branch's sources differ from a fresh build of the same Dockerfile.
RT=/opt/agenthub/session-agent
mkdir -p "$RT" /opt/agenthub/bin
grep -E '^COPY [a-z-]+(/[a-z.-]+)? /opt/session-agent/' "$SRC/agent-runtime/openclaw/Dockerfile" \
  | while read -r _ from to; do
      dest="$RT/${to#/opt/session-agent/}"
      mkdir -p "$(dirname "$dest")"
      cp -r "$SRC/agent-runtime/$from" "$dest"
      echo "COPY $from -> $dest"
    done
ln -s /opt/session-agent/node_modules "$RT/node_modules"
chmod +x "$RT"/common/*.sh "$RT"/openclaw/*.sh
cp "$PROOF/openclaw-wrapper.sh" /opt/agenthub/bin/openclaw
chmod +x /opt/agenthub/bin/openclaw
ls "$RT"

node "$PROOF/hub.mjs" > /tmp/hub.log 2>&1 &
node "$PROOF/model.mjs" > /tmp/model.log 2>&1 &
sleep 1

# The hub's skill-library entry, as McpConfigAssembler injects it.
mkdir -p /secrets/mcp
cat > /secrets/mcp/mcp.json <<JSON
{"mcpServers":{"skill-library":{"type":"http","url":"http://127.0.0.1:18092/internal/sessions/$SID/mcp","headers":{"X-Agent-Token":"$TOKEN"}}}}
JSON

echo "===== AFTER: real entrypoint, autonomous turn"
env -i PATH=/usr/local/sbin:/usr/local/bin:/usr/sbin:/usr/bin:/sbin:/bin HOME=/home/agent \
  TERMINATION_MESSAGE_PATH=/tmp/termination-log \
  AGENTHUB_SESSION_ID="$SID" AGENTHUB_MODE=autonomous AGENTHUB_AUTO_APPROVE=1 \
  AGENTHUB_PROMPT="Tell the agent Peer hello." \
  AGENTHUB_AUTH_MODE=apikey OPENAI_API_KEY=sk-proof-not-a-real-key \
  AGENTHUB_CALLBACK_URL="http://127.0.0.1:18092/internal/sessions/$SID" AGENTHUB_CALLBACK_TOKEN="$TOKEN" \
  AGENTHUB_WORKDIR=/workspace AGENTHUB_HAS_MCP=1 \
  AGENTHUB_BROWSER_ENABLED=1 AGENTHUB_SPAWN_MCP_ENABLED=1 AGENTHUB_NETWORK_MCP_ENABLED=1 \
  AGENTHUB_CONTAINERS_ENABLED=1 \
  timeout --kill-after=10 300 bash "$RT/openclaw/entrypoint.sh" > /tmp/entrypoint.log 2>&1
echo "===== entrypoint exit: $?"
tail -n 80 /tmp/entrypoint.log
echo "===== wrapper log: OpenClaw's own MCP listing from the session agent's environment"
cat /tmp/openclaw-wrapper.log

echo "===== AFTER: mcp.servers written to openclaw.json (env references only)"
node -e 'const c=require("/home/agent/.openclaw/openclaw.json"); console.log(JSON.stringify(c.mcp, null, 1))'
grep -c "$TOKEN" /home/agent/.openclaw/openclaw.json | sed 's/^/literal callback token occurrences in openclaw.json: /'

echo "===== model log"; cat /tmp/model.log
echo "===== hub log (sessions + skill-library requests)"; grep -E '/messages|/project-agents|/mcp' /tmp/hub.log
echo "===== PROOF DONE"
sleep 3600
