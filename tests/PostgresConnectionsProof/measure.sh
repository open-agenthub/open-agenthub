#!/usr/bin/env bash
# Counts the Postgres connections one real backend process holds after startup, during a short
# burst of DB-backed API requests, and once the burst is over (#377). Then stops it with SIGTERM
# and reports how it exited.
#
# Usage: measure.sh <repo-checkout> <base-connection-string> [label]
# Extra backend configuration passes through the environment, e.g. Postgres__MaxPoolSize=5.
# Needs dotnet 10 and psql on PATH. Creates and drops its own database.
set -euo pipefail

repo=$(cd "$1" && pwd)
base=$2
label=${3:-$(git -C "$repo" rev-parse --short HEAD)}
db="conn_proof_$$"
port=$((20000 + RANDOM % 20000))
work=$(mktemp -d)
trap 'kill "$pid" 2>/dev/null || true; wait "$pid" 2>/dev/null || true; dropdb_ >/dev/null 2>&1 || true; rm -rf "$work"' EXIT

host=$(sed -n 's/.*Host=\([^;]*\).*/\1/p' <<<"$base")
pgport=$(sed -n 's/.*Port=\([^;]*\).*/\1/p' <<<"$base")
user=$(sed -n 's/.*Username=\([^;]*\).*/\1/p' <<<"$base")
sql() { psql -h "$host" -p "$pgport" -U "$user" -d postgres -Atc "$1"; }
dropdb_() { sql "DROP DATABASE IF EXISTS $db WITH (FORCE)"; }
count() { sql "SELECT count(*) FROM pg_stat_activity WHERE datname = '$db'"; }

sql "CREATE DATABASE $db" >/dev/null
# Stopping the backend with a signal must not leave a core dump of several hundred MB in the
# current directory, where a later `git add -A` picks it up.
ulimit -c 0
cd "$work"

out="$work/out"
dotnet build "$repo/backend/AgentHub.Api.csproj" -c Release -o "$out" -v q -nologo >/dev/null

# Outside a cluster the backend reads ~/.kube/config; no endpoint used here reaches it.
mkdir -p "$work/home/.kube"
cat >"$work/home/.kube/config" <<'EOF'
apiVersion: v1
kind: Config
clusters: [{name: none, cluster: {server: "http://127.0.0.1:1"}}]
users: [{name: none, user: {token: unused}}]
contexts: [{name: none, context: {cluster: none, user: none, namespace: agenthub-sessions}}]
current-context: none
EOF

HOME="$work/home" ASPNETCORE_ENVIRONMENT=Production \
  Kestrel__Endpoints__Http__Url="http://127.0.0.1:$port" \
  ConnectionStrings__Postgres="${base%;};Database=$db" \
  Auth__InsecureDevMode=true AgentHub__Namespace=agenthub-sessions \
  AgentHub__CallbackBaseUrl="http://127.0.0.1:$port" Browser__Enabled=false \
  dotnet "$out/AgentHub.Api.dll" >"$work/backend.log" 2>&1 &
pid=$!

for _ in $(seq 1 240); do
  curl -fs "http://127.0.0.1:$port/healthz" >/dev/null 2>&1 && break
  kill -0 "$pid" 2>/dev/null || { cat "$work/backend.log"; exit 1; }
  sleep 0.5
done
sleep 5
after_start=$(count)
by_name() { sql "SELECT name || '=' || n FROM (SELECT coalesce(nullif(application_name, ''), '(none)') AS name, count(*) AS n FROM pg_stat_activity WHERE datname = '$db' GROUP BY 1) s ORDER BY 1" | tr '\n' ' '; }
start_names=$(by_name)

# 2000 requests, 64 in flight, over endpoints that each read a different store.
paths=(api/projects api/usage/summary api/tokens api/mcp-servers api/session-organization/order api/webhook-triggers api/skills)
for i in $(seq 1 2000); do echo "http://127.0.0.1:$port/${paths[i % ${#paths[@]}]}"; done >"$work/urls"
peak=0
started=$(date +%s%N)
( xargs -P 64 -n 1 curl -s -o /dev/null -w '%{http_code}\n' <"$work/urls" >"$work/codes" ) &
load=$!
while kill -0 "$load" 2>/dev/null; do
  c=$(count); (( c > peak )) && peak=$c
  sleep 0.2
done
wait "$load"
elapsed_ms=$(( ($(date +%s%N) - started) / 1000000 ))
sleep 2
after_load=$(count)

echo "$label: after startup=$after_start peak under load=$peak after load=$after_load load took ${elapsed_ms}ms"
echo "  by application_name after startup: $start_names"
echo "  by application_name after load: $(by_name)"
echo "  http status codes: $(sort "$work/codes" | uniq -c | tr -s ' ' | tr '\n' ',')"

# The pool is shared and never disposed by a store, so shutting down with connections still in
# it has to stay clean (#378).
kill -TERM "$pid"
exit_code=0
wait "$pid" || exit_code=$?
echo "  SIGTERM: exit code $exit_code, ObjectDisposedException in log: $(grep -c ObjectDisposedException "$work/backend.log" || true)"
