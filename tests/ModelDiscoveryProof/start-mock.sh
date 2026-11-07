#!/bin/bash
# Restart the recording mock on port $1, remembering its pid in mock-$1.pid.
cd "$(dirname "$0")"; [ -f mock-$1.pid ] && kill "$(cat mock-$1.pid)" 2>/dev/null; sleep 0.3
node mock.mjs "$1" "$PWD/m$1.log" "$PWD/rules.json" "$PWD/bundled-models.json" > mock-$1.out 2>&1 &
echo $! > mock-$1.pid
