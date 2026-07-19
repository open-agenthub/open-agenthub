#!/bin/bash
set -euo pipefail
mkdir -p /tmp/fakebin /tmp/fixture-home
cp /fixtures/fake-node-api-key.sh /tmp/fakebin/node
cp /fixtures/fake-codex-api-key.sh /tmp/fakebin/codex
chmod 755 /tmp/fakebin/node /tmp/fakebin/codex
export PATH="/tmp/fakebin:$PATH"
export HOME=/tmp/fixture-home
export AGENTHUB_MODE=interactive
export AGENTHUB_AUTH_MODE=apikey
export AGENTHUB_HAS_MCP=0
exec /usr/local/bin/entrypoint.sh
