#!/usr/bin/env bash
# Runs inside the agent PTY in place of `claude` when the entrypoint found no credential
# (AGENTHUB_CLAUDE_SIGN_IN). Checked again here because the PTY restarts the CLI: once a login
# has written .credentials.json, a restart must start the TUI, not a second sign-in.
#
# `claude auth login` rather than a `/login` typed into the TUI: it shows the paste-code screen
# at once, where /login first stops on a method chooser, and it leaves the positional argument
# free for the session's task, which the TUI then starts with the new login.

# A handler, not `trap '' INT`: an ignored signal stays ignored across exec and would reach the
# TUI. Without one, bash dies with the SIGINT that cancels the login and the TUI never starts.
trap : INT

if [ ! -s "$HOME/.claude/.credentials.json" ] && [ -z "${ANTHROPIC_API_KEY:-}" ] \
   && [ -z "${ANTHROPIC_AUTH_TOKEN:-}" ] && [ -z "${CLAUDE_CODE_OAUTH_TOKEN:-}" ] \
   && claude auth status --json 2>/dev/null | grep -Eq '"loggedIn":[[:space:]]*false'; then
  # A cancelled or failed login still starts the TUI: it says "Not logged in · Run /login",
  # which offers the method chooser this route skips.
  claude auth login || true
fi
trap - INT
exec claude "$@"
