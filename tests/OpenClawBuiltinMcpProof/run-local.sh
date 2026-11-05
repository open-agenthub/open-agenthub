#!/usr/bin/env bash
# Runs proof.sh in a session pod without docker: proot binds the paths the image has
# (/opt/agenthub, /usr/local/bin/openclaw, /secrets/mcp, /home/agent) and relocates
# /tmp, because the session's own pod keeps /tmp/agenthub-mcp.json and the auth-watcher pid there.
# PROOF_AGENT_PORT moves the session agent off 7681, which the session pod's own agent holds.
# Usage: run-local.sh <checkout> <scratch dir> <openclaw prefix with bin/openclaw>
set -euo pipefail
SRC="$(cd "${1:?checkout}" && pwd)"; S="${2:?scratch}"; OC="${3:?openclaw prefix}"
PROOT="${PROOT:-$S/bin/proot}"
rm -rf "$S/rig"; mkdir -p "$S/rig"/{opt,tmp,secrets,home,ws}
# A launcher rather than a bind of the package's bin link: node resolves a module's imports from
# its real path, and a bound file's real path is the bind target.
printf '#!/bin/sh\nexec node /usr/local/lib/node_modules/openclaw/openclaw.mjs "$@"\n' > "$S/rig/openclaw"
chmod +x "$S/rig/openclaw"
"$PROOT" -b "$S/rig/opt:/opt/agenthub" -b "$S/rig/tmp:/tmp" -b "$S/rig/secrets:/secrets/mcp" \
  -b "$S/rig/home:/home/agent" \
  -b "$OC/lib/node_modules/openclaw:/usr/local/lib/node_modules/openclaw" \
  -b "$S/rig/openclaw:/usr/local/bin/openclaw" \
  env PROOF_WORKDIR="$S/rig/ws" PROOF_AGENT_PORT=17681 bash "$SRC/tests/OpenClawBuiltinMcpProof/proof.sh" "$SRC"
