#!/bin/bash
# Resets the rig between runs: stops the backend, drops its database, deletes every secret and pod
# the backend wrote. Usage: reset.sh <admin-kubeconfig> <postgres-bin-dir> [db]
set -u
K="${KUBECTL:-kubectl} --kubeconfig $1 -n agenthub-sessions"
# No pkill in a session pod; match the executable, not a command line (which matches this shell).
for p in /proc/[0-9]*; do [ "$(readlink $p/exe 2>/dev/null)" = "$HOME/.dotnet/dotnet" ] && kill ${p#/proc/} 2>/dev/null; done
sleep 2
$K delete pods --all --wait=false >/dev/null 2>&1
$K delete secrets -l agenthub.dev/owner >/dev/null 2>&1
"$2/dropdb" -h 127.0.0.1 -p 54329 -U postgres --if-exists "${3:-agenthub253}" && "$2/createdb" -h 127.0.0.1 -p 54329 -U postgres "${3:-agenthub253}"
