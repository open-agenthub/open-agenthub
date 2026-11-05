#!/bin/bash
# Rebuilds the /opt/session-agent image layout from the worktree (re-run after every code change).
set -e
R=${R:?set R to the rig dir}; WT=${WT:?set WT to <worktree>/agent-runtime}
rm -rf $R/rt && mkdir -p $R/rt $R/etc-codex $R/tmp $R/home/.codex $R/work
cp $WT/session-agent/server.js $WT/session-agent/package.json $R/rt/
cp -r $WT/common $WT/codex $WT/files $WT/browser $R/rt/
cp -r $WT/session-agent/node_modules $R/rt/node_modules
rm -rf $R/rt/node_modules/node-pty && cp -r /opt/session-agent/node_modules/node-pty $R/rt/node_modules/node-pty
cp $WT/codex/requirements.toml $R/etc-codex/requirements.toml
[ -d $R/work/.git ] || git init -q $R/work
cp -r $WT/claude $R/rt/
cp -r $WT/opencode $R/rt/
mkdir -p $R/etc-opencode && node $WT/opencode/managed-config.js $R/etc-opencode/opencode.json /opt/session-agent/opencode >/dev/null
