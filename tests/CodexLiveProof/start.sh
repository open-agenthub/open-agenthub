#!/bin/bash
# Starts the browser proof: the rig serving the real Codex in the real session agent, the proof
# host (the real backend conversation path), the stand-in hub and Vite. Stop them by pid
# afterwards. FRONTEND picks the web app to serve (default: this checkout's).
set -eu
: "${RIG_HOME:?set RIG_HOME to an absolute scratch directory}"
P=$(cd "$(dirname "$0")" && pwd)
export AGENT_PORT=${AGENT_PORT:-17901} STORED_FILE=$RIG_HOME/stored.jsonl
export DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=1
rm -f "$STORED_FILE"
(cd "$RIG_HOME" && rm -rf tmp-run-* && SERVE=${SERVE:-400000} OUT=$RIG_HOME/out-serve node "$P/rig.mjs" > "$RIG_HOME/rig.out" 2>&1 &)
(cd "$P/Host" && dotnet build -o "$RIG_HOME/host-bin" > "$RIG_HOME/host-build.out" 2>&1 && dotnet "$RIG_HOME/host-bin/Host.dll" > "$RIG_HOME/host.out" 2>&1 &)
(cd "$RIG_HOME" && node "$P/hub.mjs" > "$RIG_HOME/hub.out" 2>&1 &)
(cd "${FRONTEND:-$P/../../frontend}" && npx vite --port 5173 --strictPort > "$RIG_HOME/vite.out" 2>&1 &)
