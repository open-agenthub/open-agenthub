#!/bin/sh
set -eu
if [ "$1" = "-e" ]; then
  exec /usr/local/bin/node "$@"
fi
if [ "$1" = "/opt/session-agent/codex/mcp-config.js" ]; then
  exec /usr/local/bin/node "$@"
fi
if [ "$1" = "/opt/session-agent/common/server.js" ]; then
  test -z "${CODEX_API_KEY+x}"
  cmp /fixtures/subscription-auth.json "$CODEX_HOME/auth.json"
  test "$(stat -c %a "$CODEX_HOME/auth.json")" = 600
  test "$(stat -c %a "$CODEX_HOME/config.toml")" = 600
  grep -Fx 'cli_auth_credentials_store = "file"' "$CODEX_HOME/config.toml" >/dev/null
  grep -Fx '[mcp_servers.fixture]' "$CODEX_HOME/config.toml" >/dev/null
  grep -Fx 'command = "fixture-command"' "$CODEX_HOME/config.toml" >/dev/null
  mcp_list=$(/usr/local/bin/node \
    /usr/local/lib/node_modules/@openai/codex/bin/codex.js mcp list)
  printf '%s\n' "$mcp_list" | grep -F fixture >/dev/null
  printf '%s\n' "$mcp_list" | grep -F fixture-command >/dev/null
  echo subscription-config-auth-fixture-ok
  exit 0
fi
exit 2
