#!/usr/bin/env bash
# Starts N copies of the REAL backend at the same moment against one Postgres database, the way
# the quickstart's two replicas start, and reports how long each takes to answer /healthz 200.
# While any copy is not healthy after STALL seconds it dumps pg_stat_activity and pg_locks.
#   run.sh <AgentHub.Api.dll> <database> <replicas> [timeout seconds]
# Env: PG_BIN (psql/createdb dir), PGPORT (54329), OUT (output dir), STALL (20)
set -u
ulimit -c 0
DLL=$1 DB=$2 N=$3 TIMEOUT=${4:-60}
PG_BIN=${PG_BIN:?} PGPORT=${PGPORT:-54329} OUT=${OUT:-./out} STALL=${STALL:-20}
mkdir -p "$OUT"
psql() { "$PG_BIN/psql" -h 127.0.0.1 -p "$PGPORT" -U postgres "$@"; }
# A random key per run: the quickstart's configuration is API key auth with no OIDC.
KEY=$(head -c 32 /dev/urandom | base64 | tr -dc 'A-Za-z0-9' | head -c 40)
pids=()
start=$(date +%s%3N)
for i in $(seq 1 "$N"); do
  port=$((18080 + i))
  ASPNETCORE_ENVIRONMENT=Production Kestrel__Endpoints__Http__Url=http://127.0.0.1:$port \
  ConnectionStrings__Postgres="Host=127.0.0.1;Port=$PGPORT;Username=postgres;Database=$DB" \
  AgentHub__Namespace=agenthub-sessions AgentHub__CallbackBaseUrl=http://127.0.0.1:$port \
  AgentHub__ClaudeAgentImage=registry.example.com/agent-claude:proof \
  AgentHub__AgentImage=registry.example.com/agent:proof Browser__Enabled=false \
  Auth__ApiKey=$KEY dotnet "$DLL" > "$OUT/backend-$i.log" 2>&1 &
  pids+=($!)
done
declare -A healthy=()
done_n=0
dumped=0
while :; do
  ms=$(( $(date +%s%3N) - start )); el=$((ms / 1000)).$(printf %03d $((ms % 1000)))
  for i in $(seq 1 "$N"); do
    [ -n "${healthy[$i]:-}" ] && continue
    code=$(curl -s -o /dev/null -w '%{http_code}' --max-time 1 http://127.0.0.1:$((18080 + i))/healthz)
    if [ "$code" = 200 ]; then healthy[$i]=$el; done_n=$((done_n+1)); echo "replica $i healthy after ${el}s"; fi
    if ! kill -0 "${pids[$((i-1))]}" 2>/dev/null && [ -z "${healthy[$i]:-}" ]; then healthy[$i]=exited; done_n=$((done_n+1)); echo "replica $i EXITED"; fi
  done
  [ "$done_n" -eq "$N" ] && break
  if [ $dumped = 0 ] && (( ms > STALL * 1000 )); then
    dumped=1
    echo "--- not healthy after ${STALL}s: pg_stat_activity / pg_locks ---"
    psql -d "$DB" -X -c "SELECT pid, state, wait_event_type, wait_event, pg_blocking_pids(pid) AS blocked_by, left(regexp_replace(query, '\s+', ' ', 'g'), 160) AS query FROM pg_stat_activity WHERE datname = '$DB' AND pid <> pg_backend_pid() ORDER BY pid" | tee "$OUT/activity.txt"
    psql -d "$DB" -X -c "SELECT l.pid, l.locktype, c.relname, l.mode, l.granted FROM pg_locks l LEFT JOIN pg_class c ON c.oid = l.relation WHERE l.database = (SELECT oid FROM pg_database WHERE datname = '$DB') AND (NOT l.granted OR c.relname NOT LIKE 'pg_%') ORDER BY l.granted, l.pid" | tee "$OUT/locks.txt"
  fi
  (( ms > TIMEOUT * 1000 )) && { echo "TIMEOUT after ${TIMEOUT}s"; break; }
  sleep 0.5
done
for p in "${pids[@]}"; do kill "$p" 2>/dev/null; done
# A deadlocked host does not finish its shutdown either.
sleep 5
for p in "${pids[@]}"; do kill -9 "$p" 2>/dev/null; done
wait 2>/dev/null
ok=0; for i in $(seq 1 "$N"); do [[ "${healthy[$i]:-}" =~ ^[0-9] ]] && ok=$((ok+1)); done
echo "RESULT $ok/$N healthy"
[ $ok = "$N" ]
