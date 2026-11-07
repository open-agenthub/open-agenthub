#!/usr/bin/env bash
# The #538 situation with the real backend: another transaction holds
# `SELECT id FROM sessions FOR UPDATE` for HOLD seconds while a backend starts, and a reader
# runs `SELECT count(*) FROM sessions` every 0.2 s. Reports when the backend is healthy and the
# slowest read.
#   held-start.sh <AgentHub.Api.dll> <database> [hold seconds (20)] [timeout seconds (60)]
# Env: PG_BIN, PGPORT (54329), OUT (./out); DataProtection__* as for upgrade.sh
set -u
ulimit -c 0
DLL=$1 DB=$2 HOLD=${3:-20} TIMEOUT=${4:-60}
PG_BIN=${PG_BIN:?} PGPORT=${PGPORT:-54329} OUT=${OUT:-./out}
HERE=$(cd "$(dirname "$0")" && pwd)
mkdir -p "$OUT"
psql() { "$PG_BIN/psql" -h 127.0.0.1 -p "$PGPORT" -U postgres -X -q -d "$DB" "$@"; }
psql -c "BEGIN; SELECT id FROM sessions FOR UPDATE; SELECT pg_sleep($HOLD); ROLLBACK;" >/dev/null &
holder=$!
sleep 0.5
( end=$(( $(date +%s) + TIMEOUT )); max=0
  while [ "$(date +%s)" -lt "$end" ]; do
    t0=$(date +%s%3N); psql -tAc "SELECT count(*) FROM sessions" >/dev/null; ms=$(( $(date +%s%3N) - t0 ))
    [ $ms -gt $max ] && max=$ms; echo $max > "$OUT/read-max-ms"; sleep 0.2
  done ) &
reader=$!
OUT=$OUT/backend "$HERE/../StartupLockProof/run.sh" "$DLL" "$DB" 1 "$TIMEOUT" | grep -E "healthy|EXITED|RESULT|TIMEOUT"
kill $reader 2>/dev/null; wait $holder 2>/dev/null
echo "lock held for ${HOLD}s; slowest read $(cat "$OUT/read-max-ms") ms"
grep -hE "Schema script|schema migration|Unhandled|TimeoutException" "$OUT/backend/backend-1.log" | cut -c1-400 | head -8
