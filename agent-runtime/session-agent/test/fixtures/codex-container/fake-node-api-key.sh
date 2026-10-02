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
# The shared entrypoint always enables the files MCP, so every start runs these three —
# even here, where the session itself brings no MCP config (AGENTHUB_HAS_MCP=0). The
# skills step runs too and finds nothing to take over, which is the point: it only ever
# replaces an entry the hub injected.
if [ "$1" = "/opt/session-agent/files/configure.mjs" ]; then
  exec /usr/local/bin/node "$@"
fi
if [ "$1" = "/opt/session-agent/skills/configure.mjs" ]; then
  exec /usr/local/bin/node "$@"
fi
if [ "$1" = "/opt/session-agent/codex/session-prompt.mjs" ]; then
  exec /usr/local/bin/node "$@"
fi
if [ "$1" = "/opt/session-agent/codex/mcp-config.js" ]; then
  exec /usr/local/bin/node "$@"
fi
echo "unhandled node invocation in fixture: $*" >&2
exit 2
