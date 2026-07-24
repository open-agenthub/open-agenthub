#!/bin/sh
set -eu
test "$1" = login
test "$2" = --with-api-key
IFS= read -r supplied
test "$supplied" = synthetic-codex-key
printf '%s\n' '{"tokens":{"access_token":"generated-synthetic-fixture"}}' > "$CODEX_HOME/auth.json"
