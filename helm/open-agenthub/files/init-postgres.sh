#!/usr/bin/env bash
set -Eeuo pipefail
# Reuse the image's password and host-auth setup, but never start its temporary
# multi-process server: stopping that server needs signals broken by stacked AppArmor peers.
source /usr/local/bin/docker-entrypoint.sh
docker_setup_env
docker_create_db_directories
pending="${PGDATA%/*}/.agenthub-init-pending"
if [ -z "$DATABASE_ALREADY_EXISTS" ]; then
  touch "$pending"
  docker_verify_minimum_env
  docker_init_database_dir
  pg_setup_hba_conf postgres
fi
if [ -f "$pending" ]; then
  # Single-user mode checkpoints and exits itself; there is no pg_ctl or checkpointer to signal.
  # Keep the marker until SQL succeeds so an interrupted init is retried before normal startup.
  # Alpine's account uses a different UID; the chart's UID 999 needs the same
  # temporary passwd mapping that the image uses for initdb, also in single-user mode.
  if ! getent passwd "$(id -u)" > /dev/null; then
    for wrapper in {/usr,}/lib{/*,}/libnss_wrapper.so; do
      if [ -s "$wrapper" ]; then
        export LD_PRELOAD="$wrapper"
        NSS_WRAPPER_PASSWD=$(mktemp)
        NSS_WRAPPER_GROUP=$(mktemp)
        export NSS_WRAPPER_PASSWD NSS_WRAPPER_GROUP
        printf 'postgres:x:%s:%s:PostgreSQL:%s:/bin/false\n' "$(id -u)" "$(id -g)" "$PGDATA" > "$NSS_WRAPPER_PASSWD"
        printf 'postgres:x:%s:\n' "$(id -g)" > "$NSS_WRAPPER_GROUP"
        trap 'rm -f "$NSS_WRAPPER_PASSWD" "$NSS_WRAPPER_GROUP"' EXIT
        break
      fi
    done
  fi
  output=$(mktemp)
  if ! postgres --single -D "$PGDATA" postgres > "$output" 2>&1 <<'SQL'
CREATE DATABASE agenthub;
SQL
  then
    cat "$output"
    exit 1
  fi
  cat "$output"
  if grep -E 'ERROR:|FATAL:|PANIC:' "$output" | grep -v 'database "agenthub" already exists'; then
    exit 1
  fi
  rm "$output" "$pending"
fi
