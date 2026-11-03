#!/usr/bin/env bash
set -euo pipefail
HERE="$(cd -- "$(dirname -- "$0")" && pwd)"
: "${RIG_HOME:?scratch directory with clis/node_modules/.bin/claude}"
export RUNTIME="${PROOF_RUNTIME:-$(cd "$HERE/../../agent-runtime" && pwd)}"
export NODE_MODULES="${PROOF_NODE_MODULES:-$RUNTIME/session-agent/node_modules}"
mkdir -p "$RIG_HOME/model"
cp "$HERE/model.mjs" "$RIG_HOME/model/"
openssl req -x509 -newkey rsa:2048 -nodes -keyout "$RIG_HOME/model/ca.key" -out "$RIG_HOME/model/ca.pem" -days 3 -subj /CN=rig-ca 2>/dev/null
openssl req -newkey rsa:2048 -nodes -keyout "$RIG_HOME/model/leaf.key" -out "$RIG_HOME/model/leaf.csr" -subj /CN=api.anthropic.com 2>/dev/null
printf 'subjectAltName=DNS:platform.claude.com,DNS:api.anthropic.com,DNS:claude.ai,DNS:*.anthropic.com,DNS:*.claude.com\n' > "$RIG_HOME/model/ext.cnf"
openssl x509 -req -in "$RIG_HOME/model/leaf.csr" -CA "$RIG_HOME/model/ca.pem" -CAkey "$RIG_HOME/model/ca.key" -CAcreateserial -out "$RIG_HOME/model/leaf.pem" -days 3 -extfile "$RIG_HOME/model/ext.cnf" 2>/dev/null
export CA_CERT="$RIG_HOME/model/ca.pem"
export PRIVACY_PROXY=http://127.0.0.1:19643
PROXY_PORT=19643 ADMIN_PORT=19644 node "$RIG_HOME/model/model.mjs" > "$RIG_HOME/model.log" 2>&1 &
model_pid=$!
trap 'kill "$model_pid" 2>/dev/null || true' EXIT
sleep 1
ASK=1 OAUTH=1 AUTO=0 PREALLOW=1 node "$HERE/pty-rig.mjs" claude '[{"wait":3500},{"paste":"EMAIL-PROBE"},{"wait":6500},{"dump":"screen"}]' interactive
ASK=1 OAUTH=1 AUTO=1 node "$HERE/pty-rig.mjs" claude '[{"wait":3500},{"paste":"EMAIL-PROBE"},{"wait":6500},{"dump":"screen"}]' auto
ASK=1 OAUTH=1 AUTO=1 MODE=autonomous node "$HERE/pty-rig.mjs" claude '[{"wait":10000},{"dump":"screen"}]' autonomous
node "$HERE/check.mjs"
