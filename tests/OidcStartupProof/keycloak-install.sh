#!/usr/bin/env bash
set -euo pipefail
proof_dir=$(cd "$(dirname "$0")" && pwd)
output=$(mktemp -d)
chmod 755 "$output"
node_ip=$(kubectl get nodes -o jsonpath='{.items[0].status.addresses[?(@.type=="InternalIP")].address}')
export PROOF_ISSUER="http://$node_ip:18083/realms/team"
export PROOF_LOGIN_PASSWORD
PROOF_LOGIN_PASSWORD=$(openssl rand -hex 16)
export PROOF_REALM_FILE="$output/realm.json"
node --input-type=module <<'JS'
import fs from 'node:fs'
const source = fs.readFileSync('tests/SalesPathAuditProof/keycloak.yaml', 'utf8')
const block = source.split('  realm.json: |\n')[1].split('\n---')[0]
const realm = JSON.parse(block.split('\n').map(line => line.replace(/^    /, '')).join('\n'))
const client = realm.clients[0]
client.directAccessGrantsEnabled = false
client.redirectUris = ['http://127.0.0.1:18080/auth/callback']
client.webOrigins = ['http://127.0.0.1:18080']
client.attributes['post.logout.redirect.uris'] = 'http://127.0.0.1:18080'
realm.users = [{ username: 'team-admin', email: 'admin@example.com', firstName: 'Team', lastName: 'Admin', enabled: true, emailVerified: true, realmRoles: ['offline_access'], credentials: [{ type: 'password', value: process.env.PROOF_LOGIN_PASSWORD, temporary: false }] }]
fs.writeFileSync(process.env.PROOF_REALM_FILE, JSON.stringify(realm), { mode: 0o644 })
JS
previous=$(helm history agenthub -n agenthub -o json | jq -r '[.[] | select(.status == "deployed")][-1].revision')
# Reuse the audit's pinned, test-only identity image rather than maintaining another pin.
image=$(sed -n 's/^ *image: //p' tests/SalesPathAuditProof/keycloak.yaml)
cleanup() { docker rm -f oidc-proof-idp >/dev/null 2>&1 || true; if [ -n "${proxy_pid:-}" ]; then kill "$proxy_pid" 2>/dev/null || true; fi; }
trap cleanup EXIT
docker run -d --name oidc-proof-idp --network host --memory=1g \
  -v "$output:/opt/keycloak/data/import:ro" "$image" \
  start-dev --import-realm --http-port=18083 --hostname="http://$node_ip:18083" >/dev/null
ready=false
for i in $(seq 1 120); do
  if curl -fsS "$PROOF_ISSUER/.well-known/openid-configuration" >"$output/discovery.json"; then ready=true; break; fi
  sleep 2
done
if [ "$ready" != true ]; then docker logs oidc-proof-idp; exit 1; fi
helm upgrade agenthub ./helm/open-agenthub -n agenthub --reuse-values \
  --set "oidc.authority=$PROOF_ISSUER" --set oidc.requireHttpsMetadata=false \
  --set oidc.clientId=agenthub --set oidc.audience=agenthub --set ee.admins=team-admin \
  --wait --timeout 3m
kubectl port-forward -n agenthub svc/agenthub-frontend 18080:80 >"$output/proxy.log" 2>&1 &
proxy_pid=$!
for i in $(seq 1 30); do if curl -fsS http://127.0.0.1:18080/api/config >/dev/null; then break; fi; sleep 1; done
(cd browser-runtime && npm ci && npx playwright-core install --with-deps chromium)
node "$proof_dir/keycloak-browser.mjs"
helm rollback agenthub "$previous" -n agenthub --wait --timeout 3m
echo 'PASS: real identity-service browser proof completed; original quickstart restored'
