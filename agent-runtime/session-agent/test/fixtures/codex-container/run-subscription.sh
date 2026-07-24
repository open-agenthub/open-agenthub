#!/bin/bash
set -euo pipefail
mkdir -p /tmp/fakebin /tmp/fixture-home
cp /fixtures/fake-node-subscription.sh /tmp/fakebin/node
chmod 755 /tmp/fakebin/node
export PATH="/tmp/fakebin:$PATH"
export HOME=/tmp/fixture-home
export AGENTHUB_MODE=autonomous
export AGENTHUB_AUTH_MODE=subscription
export AGENTHUB_HAS_MCP=1
exec /usr/local/bin/entrypoint.sh
