#!/usr/bin/env bash
# Upgrades from a fresh database, a 1.7.0 one and one left by the previous release to this
# branch's backend, with the real backends started as two replicas (../StartupLockProof/run.sh),
# then compares each schema with the fresh one and starts the branch again to time a restart
# on a current schema.
#   upgrade.sh <branch dll> <v1.7.0 dll> <previous-release dll>
# Env: PG_BIN, PGPORT (54329), OUT (./out)
set -eu
ulimit -c 0
BRANCH=$1 V170=$2 PREVIOUS=$3
PG_BIN=${PG_BIN:?} PGPORT=${PGPORT:-54329} OUT=${OUT:-./out}
HERE=$(cd "$(dirname "$0")" && pwd)
RUN=$HERE/../StartupLockProof/run.sh
mkdir -p "$OUT"
# Releases after 1.7.0 refuse to start without a data-protection certificate; a throwaway one
# serves every backend here, as one key ring has to.
if [ -z "${DataProtection__CertificatePem:-}" ]; then
  openssl req -x509 -newkey rsa:2048 -nodes -keyout "$OUT/dp.key" -out "$OUT/dp.crt" -days 2 -subj /CN=proof.example.com 2>/dev/null
  export DataProtection__CertificatePem="$(cat "$OUT/dp.crt")" DataProtection__PrivateKeyPem="$(cat "$OUT/dp.key")"
fi
psql() { "$PG_BIN/psql" -h 127.0.0.1 -p "$PGPORT" -U postgres -X -q -v ON_ERROR_STOP=1 "$@"; }
suffix=$(date +%s)
for base in fresh v170 previous; do
  db=upgrade_${base}_$suffix
  "$PG_BIN/createdb" -h 127.0.0.1 -p "$PGPORT" -U postgres "$db"
  case $base in
    v170) OUT=$OUT/$base-base "$RUN" "$V170" "$db" 1 60 | tail -1 ;;
    previous) OUT=$OUT/$base-base "$RUN" "$PREVIOUS" "$db" 1 60 | tail -1 ;;
  esac
  echo "== $base -> branch, 2 replicas"
  OUT=$OUT/$base-upgrade "$RUN" "$BRANCH" "$db" 2 90 | grep -E "healthy|EXITED|RESULT"
  echo "== $base: branch restart, 1 replica"
  OUT=$OUT/$base-restart "$RUN" "$BRANCH" "$db" 1 60 | grep -E "healthy|EXITED|RESULT"
  psql -d "$db" -f "$HERE/catalogue.sql" > "$OUT/$base.catalogue"
  echo "$base: $(wc -l < "$OUT/$base.catalogue") catalogue lines, $(psql -d "$db" -tAc 'SELECT count(*) FROM schema_scripts') scripts recorded"
  "$PG_BIN/dropdb" -h 127.0.0.1 -p "$PGPORT" -U postgres --force "$db"
done
for base in v170 previous; do
  if diff "$OUT/fresh.catalogue" "$OUT/$base.catalogue" > "$OUT/$base.diff"; then echo "$base: schema identical to fresh"
  else echo "$base: schema DIFFERS from fresh:"; cat "$OUT/$base.diff"; fi
done
