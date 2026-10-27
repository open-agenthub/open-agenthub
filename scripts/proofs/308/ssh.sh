#!/usr/bin/env bash
set -euo pipefail
TASK_ROOT=${TASK_ROOT:-/tmp/agenthub-video-rc5}
exec ssh -i "$TASK_ROOT/private/demo-ssh" -p 2222 -o ConnectTimeout=10 -o ServerAliveInterval=10 -o ServerAliveCountMax=3 -o BatchMode=yes -o StrictHostKeyChecking=accept-new -o "UserKnownHostsFile=$TASK_ROOT/private/known_hosts" demo@127.0.0.1 "$@"
