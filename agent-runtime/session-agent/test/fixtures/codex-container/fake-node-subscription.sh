#!/bin/sh
set -eu
if [ "$1" = "-e" ]; then
  exec /usr/local/bin/node "$@"
fi
if [ "$1" = "/opt/session-agent/codex/mcp-config.js" ]; then
  exec /usr/local/bin/node "$@"
fi
# The shared entrypoint always enables the files MCP, so every start runs this.
if [ "$1" = "/opt/session-agent/files/configure.mjs" ]; then
  exec /usr/local/bin/node "$@"
fi
if [ "$1" = "/opt/session-agent/browser/configure-claude.mjs" ]; then
  exec /usr/local/bin/node "$@"
fi
if [ "$1" = "/opt/session-agent/sessions/configure.mjs" ]; then
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
  # Runtime-owned MCP servers must forward the backend callback variables,
  # because Codex spawns MCP subprocesses with a cleared environment.
  grep -Fx '[mcp_servers.agenthub_browser]' "$CODEX_HOME/config.toml" >/dev/null
  grep -Fx 'env_vars = ["AGENTHUB_CALLBACK_URL", "AGENTHUB_CALLBACK_TOKEN"]' \
    "$CODEX_HOME/config.toml" >/dev/null
  grep -Fx '[mcp_servers.agenthub_sessions]' "$CODEX_HOME/config.toml" >/dev/null
  grep -Fx 'env_vars = ["AGENTHUB_CALLBACK_URL", "AGENTHUB_CALLBACK_TOKEN", "AGENTHUB_SESSION_ID"]' \
    "$CODEX_HOME/config.toml" >/dev/null
  grep -Fx '[mcp_servers.agenthub_files]' "$CODEX_HOME/config.toml" >/dev/null
  grep -Fx 'env_vars = ["AGENTHUB_CALLBACK_URL", "AGENTHUB_CALLBACK_TOKEN", "AGENTHUB_WORKDIR", "AGENTHUB_FILE_ROOT", "RUNTIME"]' \
    "$CODEX_HOME/config.toml" >/dev/null
  mcp_list=$(/usr/local/bin/node \
    /usr/local/lib/node_modules/@openai/codex/bin/codex.js mcp list)
  printf '%s\n' "$mcp_list" | grep -F fixture >/dev/null
  printf '%s\n' "$mcp_list" | grep -F fixture-command >/dev/null
  # `codex mcp list` parses config.toml, so it also proves the pinned CLI
  # accepts the env_vars field on the runtime-owned server entries.
  printf '%s\n' "$mcp_list" | grep -F agenthub_browser >/dev/null
  printf '%s\n' "$mcp_list" | grep -F agenthub_sessions >/dev/null
  printf '%s\n' "$mcp_list" | grep -F agenthub_files >/dev/null
  echo subscription-config-auth-fixture-ok
  exit 0
fi
exit 2
