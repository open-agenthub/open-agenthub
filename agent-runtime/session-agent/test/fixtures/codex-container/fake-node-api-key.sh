#!/bin/sh
set -eu
if [ "$1" = "/opt/session-agent/common/server.js" ]; then
  test -z "${CODEX_API_KEY+x}"
  test ! -e /tmp/watcher-called
  test -f "$CODEX_HOME/auth.json"
  test "$(stat -c %a "$CODEX_HOME/auth.json")" = 600
  grep -Fx 'cli_auth_credentials_store = "file"' "$CODEX_HOME/config.toml" >/dev/null
  echo api-key-interactive-fixture-ok
  exit 0
fi
if [ "$1" = "/opt/session-agent/codex/auth-watcher.js" ]; then
  touch /tmp/watcher-called
fi
exit 2
