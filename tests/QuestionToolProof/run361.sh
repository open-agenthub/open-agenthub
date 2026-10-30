#!/bin/bash
# usage: run361.sh <claude|codex> <sa361|samain> <label>   (Vite serves the frontend under test on :5173)
E=$(dirname $(readlink -f $0)); S=${RIG_HOME:?set RIG_HOME to a scratch dir with clis/, proot/proot, chrome.env and the build.sh outputs}; export RIG_HOME=$S; cd $S
node $E/stop.mjs >/dev/null; rm -f hub.log
export ASK="$(cat ${ASK_FILE:-$E/long1.json})" CODEX_ASK="$(cat $E/cask1.json)"
Name=$(node -e 'const a=process.argv[1];console.log({claude:"Claude",codex:"Codex"}[a])' $1)
SA=$2 node $E/agent343.mjs $1 > agent-$1.out 2>&1 &
node $E/hub343.mjs $Name > hub.out 2>&1 &
sleep 2
set -a; . $S/chrome.env; set +a; export LANG=C.UTF-8 LC_ALL=C.UTF-8
timeout 600 node $E/browser361.mjs $1 $3
status=$?
cp run-$1/agent.log logs-361-$1-$3-agent.log 2>/dev/null; cp hub.log logs-361-$1-$3-hub.log 2>/dev/null
node $E/stop.mjs >/dev/null
exit $status
