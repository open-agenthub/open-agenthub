#!/usr/bin/env bash
# Runs proof.sh in a session pod without docker or root. proot binds the paths the image has
# (/opt/agenthub, /usr/local/bin/openclaw, /secrets/mcp, /home/agent) and relocates /tmp, because
# the session pod's own agent keeps /tmp/agenthub-mcp.json there; PROOF_AGENT_PORT moves the
# session agent off 7681, which the pod's own agent holds.
# Usage: run-local.sh <checkout> <scratch dir> <openclaw npm prefix>   (PROOT=<path to proot>)
set -euo pipefail
SRC="$(cd "${1:?checkout}" && pwd)"; S="${2:?scratch}"; OC="${3:?openclaw prefix}"
PROOT="${PROOT:-$S/bin/proot}"
rm -rf "$S/rig446"; mkdir -p "$S/rig446"/{opt,tmp,secrets,home,ws}
# LEGACY_STATE=<state.tgz written by an older runtime> resumes from that archive instead of a run 1.
LEGACY=""
if [ -n "${LEGACY_STATE:-}" ]; then cp "$LEGACY_STATE" "$S/rig446/tmp/legacy-state.tgz"; LEGACY=/tmp/legacy-state.tgz; fi
# node resolves a module's imports from its real path, so the package directory is bound and a
# launcher stands in for the bin link.
printf '#!/bin/sh\nexec node /usr/local/lib/node_modules/openclaw/openclaw.mjs "$@"\n' > "$S/rig446/openclaw"
chmod +x "$S/rig446/openclaw"
"$PROOT" -b "$S/rig446/opt:/opt/agenthub" -b "$S/rig446/tmp:/tmp" -b "$S/rig446/secrets:/secrets/mcp" \
  -b "$S/rig446/home:/home/agent" \
  -b "$OC/lib/node_modules/openclaw:/usr/local/lib/node_modules/openclaw" \
  -b "$S/rig446/openclaw:/usr/local/bin/openclaw" \
  env LEGACY_STATE="$LEGACY" PROOF_WORKDIR="$S/rig446/ws" PROOF_AGENT_PORT=17682 bash "$SRC/tests/McpConfigStateProof/proof.sh" "$SRC"
