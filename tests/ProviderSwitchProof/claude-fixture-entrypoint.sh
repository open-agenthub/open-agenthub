#!/bin/sh
set -eu
if [ "${AGENTHUB_AUTH_MODE:-}" = subscription ]; then
  # The compiled profile host is independent of the redirected model endpoint. Seed the
  # synthetic native profile cache so this proof never depends on a real account profile.
  node - <<'JS'
const fs=require('node:fs'),path=require('node:path');
const file=path.join(process.env.HOME,'.claude.json');
let config={};try{config=JSON.parse(fs.readFileSync(file,'utf8'));}catch{}
config.oauthAccount={accountUuid:'11111111-1111-4111-8111-111111111111',emailAddress:'proof@example.com',organizationUuid:'22222222-2222-4222-8222-222222222222',organizationName:'Proof workspace'};
fs.writeFileSync(file,JSON.stringify(config),{mode:0o600});
JS
fi
exec /usr/local/bin/entrypoint.sh "$@"
