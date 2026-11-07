#!/usr/bin/env bash
# The #538 situation with the real backend: another transaction holds
# `SELECT id FROM sessions FOR UPDATE` for HOLD seconds while a backend starts, and a reader
# runs `SELECT count(*) FROM sessions` every 0.2 s. Reports when the backend is healthy and the
# slowest read, and samples pg_locks for relation locks that are waiting.
# HOLD_SQL replaces the held statement, e.g. ROW EXCLUSIVE on every table: that conflicts with
# every lock DDL takes (SHARE for CREATE INDEX and up), so a start that finishes while it is held
# took none of them on any table.
#   held-start.sh <AgentHub.Api.dll> <database> [hold seconds (20)] [timeout seconds (60)]
# Env: PG_BIN, PGPORT (54329), OUT (./out); DataProtection__* as for upgrade.sh
set -u
ulimit -c 0
DLL=$1 DB=$2 HOLD=${3:-20} TIMEOUT=${4:-60}
PG_BIN=${PG_BIN:?} PGPORT=${PGPORT:-54329} OUT=${OUT:-./out}
HERE=$(cd "$(dirname "$0")" && pwd)
mkdir -p "$OUT"
psql() { "$PG_BIN/psql" -h 127.0.0.1 -p "$PGPORT" -U postgres -X -q -d "$DB" "$@"; }
HOLD_SQL=${HOLD_SQL:-SELECT id FROM sessions FOR UPDATE}
psql -c "BEGIN; $HOLD_SQL; SELECT pg_sleep($HOLD); ROLLBACK;" >/dev/null &
holder=$!
sleep 0.5
( end=$(( $(date +%s) + TIMEOUT )); max=0
  while [ "$(date +%s)" -lt "$end" ]; do
    t0=$(date +%s%3N); psql -tAc "SELECT count(*) FROM sessions" >/dev/null; ms=$(( $(date +%s%3N) - t0 ))
    [ $ms -gt $max ] && max=$ms; echo $max > "$OUT/read-max-ms"; sleep 0.2
  done ) &
reader=$!
( end=$(( $(date +%s) + TIMEOUT )); : > "$OUT/waiting-locks"
  while [ "$(date +%s)" -lt "$end" ]; do
    psql -tAc "SELECT string_agg(DISTINCT mode || ' on ' || relation::regclass, ', ') FROM pg_locks WHERE NOT granted AND locktype = 'relation'" \
      | grep . >> "$OUT/waiting-locks"; sleep 0.1
  done ) &
sampler=$!
OUT=$OUT/backend "$HERE/../StartupLockProof/run.sh" "$DLL" "$DB" 1 "$TIMEOUT" | grep -E "healthy|EXITED|RESULT|TIMEOUT"
kill $reader $sampler 2>/dev/null; wait $holder 2>/dev/null
echo "lock held for ${HOLD}s; slowest read $(cat "$OUT/read-max-ms") ms"
echo "waiting relation locks seen: $(sort "$OUT/waiting-locks" | uniq -c | tr '\n' ';' | sed 's/^$/none/')"
grep -hE "Schema script|schema migration|Unhandled|TimeoutException" "$OUT/backend/backend-1.log" | cut -c1-400 | head -8
