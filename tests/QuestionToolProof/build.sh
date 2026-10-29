#!/bin/bash
# Image layout of /opt/session-agent from an agent-runtime tree: build.sh <agent-runtime dir> <out dir>
set -e
WT=$1; O=$2; rm -rf $O && mkdir -p $O/rt $O/etc-codex $O/etc-opencode
cp $WT/session-agent/server.js $WT/session-agent/package.json $O/rt/
cp -r $WT/common $WT/codex $WT/claude $WT/opencode $WT/files $WT/browser $O/rt/
cp -r /opt/session-agent/node_modules $O/rt/node_modules
cp $WT/codex/requirements.toml $O/etc-codex/requirements.toml
node $WT/opencode/managed-config.js $O/etc-opencode/opencode.json /opt/session-agent/opencode >/dev/null
# As the Dockerfiles do: notify-hook.sh is 644 in git, and a hook that is not executable never runs.
chmod +x $O/rt/claude/hooks/*.sh $O/rt/common/*.sh
