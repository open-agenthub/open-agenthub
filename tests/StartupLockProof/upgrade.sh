#!/usr/bin/env bash
# Prepares a 1.7.0-shaped database for run.sh: the 1.7.0 backend creates its schema, then
# metadata rows whose session no longer exists are added, as an install that deleted sessions
# before #367/#368 has them. Those migrations prune them and add the foreign keys.
#   upgrade.sh <1.7.0 AgentHub.Api.dll> <database>
# Env: PG_BIN, PGPORT (54329)
set -eu
ulimit -c 0
DLL=$1 DB=$2 PG_BIN=${PG_BIN:?} PGPORT=${PGPORT:-54329}
psql() { "$PG_BIN/psql" -h 127.0.0.1 -p "$PGPORT" -U postgres -X -q -v ON_ERROR_STOP=1 -d "$DB" "$@"; }
"$PG_BIN/createdb" -h 127.0.0.1 -p "$PGPORT" -U postgres "$DB"
OUT=$(mktemp -d) "$(dirname "$0")/run.sh" "$DLL" "$DB" 1 60 >/dev/null
psql <<'SQL'
INSERT INTO sessions (id, owner, title, mode, status, callback_token, agent_session_id)
VALUES ('live0001', 'admin', 'live', 'Interactive', 'Stopped', 'cb-live', 'a1');
INSERT INTO session_shares (session_id, recipient_owner, role) VALUES ('gone0001', 'bob', 'Viewer'), ('live0001', 'bob', 'Viewer');
INSERT INTO session_share_links (id, session_id, token_hash, role) VALUES ('l1', 'gone0001', '\x01', 'Viewer');
INSERT INTO session_mcp_policies (session_id, blocked_servers, blocked_tools) VALUES ('gone0001', '[]', '[]');
INSERT INTO slack_threads (session_id, owner, channel, thread_ts) VALUES ('gone0001', 'admin', 'C1', '1.1');
INSERT INTO chat_session_bindings (platform, session_id, owner, chat_id, active) VALUES ('telegram', 'gone0001', 'admin', '5', TRUE);
SQL
psql -c "SELECT conname FROM pg_constraint WHERE conname LIKE '%session_fk'" -c "SELECT to_regclass('chat_deleted_sessions') AS tombstones"
