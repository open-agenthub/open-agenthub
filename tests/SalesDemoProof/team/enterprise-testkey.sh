#!/usr/bin/env bash
# DEMO ONLY, never for a real install: Enterprise on the demo instance without a production
# licence. Mounts a 1.19.0 backend build whose only change is the licence verification key
# (../../SalesPathAuditProof/licence/README.md) over the image's /app, and turns the licence
# heartbeat off (ee.license.serviceUrl="") so the test token is never sent to the production
# licence service. Then signs a token with the throwaway key and activates it as alice.
#   BIN   the key-patched build output (default ~/rig/testkey-backend-bin)
#   KEY   the throwaway private key   (default ~/rig/lic/priv.pem)
set -euo pipefail
here=$(cd "$(dirname "$0")" && pwd); rig="$here/../../SalesPathAuditProof"
BIN=${BIN:-$HOME/rig/testkey-backend-bin}; KEY=${KEY:-$HOME/rig/lic/priv.pem}
cd "$rig"
tar -C "$BIN" -czf - . | bash ssh.sh 'sudo mkdir -p /opt/testkey-backend && sudo tar -C /opt/testkey-backend -xzf - && echo copied'
# sign-token.py signs a fixed organisation; this signs the same format with the demo label.
TOKEN=$(sed 's/"Audit Example"/"Demo Example — TEST KEY, not a real licence"/; s/audit@example.com/sales-demo@example.com/' licence/sign-token.py \
  | python3 - "$KEY" $((60*60*24*30)))
bash ssh.sh "TOKEN='$TOKEN' bash -s" <<'GUEST'
set -e; export KUBECONFIG=/etc/rancher/k3s/k3s.yaml
helm upgrade agenthub agenthub/open-agenthub -n agenthub --reset-then-reuse-values -f team-values.yaml \
  --set ingress.className=traefik --set-string ee.license.serviceUrl= --wait --timeout 5m >/dev/null
kubectl -n agenthub patch deployment agenthub-backend --type=strategic -p '{"spec":{"template":{"spec":{
  "volumes":[{"name":"testkey-backend","hostPath":{"path":"/opt/testkey-backend","type":"Directory"}}],
  "containers":[{"name":"backend","volumeMounts":[{"name":"testkey-backend","mountPath":"/app","readOnly":true}]}]}}}}'
kubectl -n agenthub rollout status deploy/agenthub-backend --timeout=6m
T=$(curl -s -d "grant_type=password&client_id=agenthub&username=alice&password=alice-demo-pw&scope=openid" \
  https://id.example.com/realms/team/protocol/openid-connect/token | python3 -c 'import sys,json;print(json.load(sys.stdin)["access_token"])')
curl -s -X POST https://hub.example.com/api/admin/license -H "Authorization: Bearer $T" -H 'content-type: application/json' \
  -d "{\"token\":\"$TOKEN\"}"; echo
GUEST
