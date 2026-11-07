#!/usr/bin/env bash
# In the guest: alice (signed in through the IdP stand-in; the password grant returns the same kind
# of access token the browser gets) starts an Interactive Claude session on the stand-in model and
# repository and shares it with bob as Collaborator, through the endpoints the UI calls.
#   UI_MODE=terminal (default; what the New-session dialog creates for both Workspace and Terminal)
#   UI_MODE=chat     (the older streaming transport the API still accepts)
#   TITLE="..."      session title
set -u
cd "$(dirname "$0")"
bash ssh.sh "UI_MODE='${UI_MODE:-terminal}' TITLE='${TITLE:-Pairing}' bash -s" <<'GUEST'
TOK=$(curl -s -d "grant_type=password&client_id=agenthub&username=alice&password=alice-audit-pw&scope=openid" \
  http://kc.example.test:18081/realms/team/protocol/openid-connect/token \
  | python3 -c 'import sys,json;print(json.load(sys.stdin)["access_token"])')
H="Authorization: Bearer $TOK"
export UI_MODE TITLE
BODY=$(python3 -c 'import json, os
print(json.dumps({"title": os.environ["TITLE"], "mode": "Interactive", "uiMode": os.environ["UI_MODE"],
  "agent": "Claude", "authMode": "CustomEndpoint",
  "modelEndpointUrl": "http://model.audit.svc.cluster.local:443", "modelName": "audit-model",
  "repos": [{"url": "git://gitd.audit.svc.cluster.local:443/demo.git"}], "prompt": "hello from alice"}))')
ID=$(curl -s -X POST http://localhost:8080/api/sessions -H "$H" -H "content-type: application/json" -d "$BODY" \
  | python3 -c 'import sys,json;print(json.load(sys.stdin)["id"])')
echo "session $ID"
curl -s -X POST "http://localhost:8080/api/ee/sessions/$ID/shares/users" -H "$H" -H "content-type: application/json" \
  -d '{"recipient":"bob","role":"collaborator"}' | head -c 300; echo
GUEST
