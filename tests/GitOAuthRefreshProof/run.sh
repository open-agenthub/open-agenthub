#!/bin/bash
# One run: fresh database, empty namespace, fresh mock GitLab, the kubelet stand-in, the given
# backend build, then proof.mjs. Everything it starts is stopped at the end.
# Usage: run.sh <rig> <repo> <AgentHub.Api.dll> <database> <phase> ['{"Extra__Setting":"value"}'] [mock env...]
set -u
RIG=$1 REPO=$2 DLL=$3 DB=$4 PHASE=$5 EXTRA=${6:-'{}'}; shift 6 || shift $#
HERE=$(cd "$(dirname "$0")" && pwd)
KC=$RIG/k8s/kubeconfig K="$HOME/k8s/kubectl --kubeconfig $KC -n agenthub-sessions"
export KUBECTL=$HOME/k8s/kubectl
PIDS=()
stop() { for p in "${PIDS[@]}"; do kill "$p" 2>/dev/null; done; for f in $RIG/pods/*/agent.pid; do [ -f "$f" ] && kill "$(cat $f)" 2>/dev/null; done; }
trap stop EXIT

$K delete pods --all --grace-period=0 --force >/dev/null 2>&1
$K delete secrets -l agenthub.dev/owner >/dev/null 2>&1
$K get secrets -o name | grep -E 'gitauth-|gitcreds-' | xargs -r $K delete >/dev/null 2>&1
rm -rf $RIG/pods $RIG/*.log $RIG/mock/events.log
"$PG_BIN/createdb" -h 127.0.0.1 -p 54329 -U postgres "$DB" 2>/dev/null || true

env "$@" node $HERE/mock-gitlab.mjs $RIG/mock & PIDS+=($!)
node $HERE/kubelet.mjs $RIG $REPO $KC & PIDS+=($!)
node $HERE/backend.mjs "$DLL" $RIG "$DB" "$EXTRA" > $RIG/backend.log 2>&1 & PIDS+=($!)
until curl -fs -o /dev/null http://127.0.0.1:8080/api/config; do sleep 1; done
NODE_EXTRA_CA_CERTS=$RIG/mock/certs/ca.crt node $HERE/proof.mjs "$PHASE" $RIG $KC ${SECONDS_ARG:-}
STATUS=$?
exit $STATUS
