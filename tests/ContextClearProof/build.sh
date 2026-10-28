#!/bin/bash
# Lays out the /opt/session-agent image tree from an agent-runtime checkout (main or branch).
# Re-run after every code change: everything is copied.
#   R   rig directory (scratch, outside the repository)
#   WT  <checkout>/agent-runtime whose code is under test
#   NM  node_modules to use (default: $WT/session-agent/node_modules, from `npm ci --ignore-scripts`)
set -e
R=${R:?set R}; WT=${WT:?set WT}; NM=${NM:-$WT/session-agent/node_modules}
rm -rf "$R/rt" "$R/etc-codex" && mkdir -p "$R/rt" "$R/etc-codex"
cp "$WT/session-agent/server.js" "$WT/session-agent/package.json" "$R/rt/"
cp -r "$WT/common" "$WT/codex" "$WT/claude" "$WT/files" "$WT/browser" "$WT/sessions" "$R/rt/"
cp -r "$NM" "$R/rt/node_modules"
# node-pty does not build in a session pod; the image's built copy is used.
rm -rf "$R/rt/node_modules/node-pty" && cp -r /opt/session-agent/node_modules/node-pty "$R/rt/node_modules/node-pty"
cp "$WT/codex/requirements.toml" "$R/etc-codex/requirements.toml"
cp "$(dirname "$0")/fault-server.cjs" "$R/rt/"
