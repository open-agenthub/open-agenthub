#!/bin/bash
# usage: run343.sh <ask|idle> <claude|codex|opencode> <sa343|samain> <label>   (Vite serves the frontend under test on :5173)
E=$(dirname $(readlink -f $0)); S=${RIG_HOME:?set RIG_HOME to a scratch dir with clis/, proot/proot, chrome.env and the build.sh outputs}; export RIG_HOME=$S; cd $S
node $E/stop.mjs >/dev/null; rm -f hub.log
export ASK="$(cat ${ASK_FILE:-$E/long1.json})" CODEX_ASK="$(cat $E/cask1.json)" OC_ASK="$(cat $E/ocask1.json)"
Name=$(node -e 'const a=process.argv[1];console.log({claude:"Claude",codex:"Codex",opencode:"OpenCode"}[a])' $2)
SA=$3 node $E/agent343.mjs $2 > agent-$2.out 2>&1 &
node $E/hub343.mjs $Name > hub.out 2>&1 &
sleep 2
set -a; . $S/chrome.env; set +a; export LANG=C.UTF-8 LC_ALL=C.UTF-8
timeout 600 node $E/browser343.mjs $1 $2 $4
status=$?
cp run-$2/agent.log logs-$1-$2-$4-agent.log 2>/dev/null; cp hub.log logs-$1-$2-$4-hub.log 2>/dev/null
node $E/stop.mjs >/dev/null
exit $status
