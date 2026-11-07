#!/usr/bin/env bash
set -euo pipefail
output=$(mktemp -d)
previous=$(helm history agenthub -n agenthub -o json | jq -r '[.[] | select(.status == "deployed")][-1].revision')
node_ip=$(kubectl get nodes -o jsonpath='{.items[0].status.addresses[?(@.type=="InternalIP")].address}')
export PROOF_ISSUER="http://$node_ip:18081"
export PROOF_API=https://hub.example.com
openssl req -x509 -newkey rsa:2048 -nodes -keyout "$output/tls.key" -out "$output/tls.crt" -days 1 -subj '/CN=hub.example.com' -addext 'subjectAltName=DNS:hub.example.com'
kubectl create secret tls team-proof-tls -n agenthub --cert="$output/tls.crt" --key="$output/tls.key" --dry-run=client -o yaml | kubectl apply -f -
node tests/OidcStartupProof/issuer.mjs >"$output/issuer.log" 2>&1 &
issuer_pid=$!
trap 'kill "$issuer_pid" 2>/dev/null || true' EXIT
for i in $(seq 1 30); do if curl -fsS "$PROOF_ISSUER/.well-known/openid-configuration" >/dev/null; then break; fi; sleep 1; done
# The class is the production migration choice; HTTP metadata is only for this isolated issuer.
args=(agenthub ./helm/open-agenthub -n agenthub --reuse-values --set "oidc.authority=$PROOF_ISSUER" --set oidc.requireHttpsMetadata=false --set oidc.clientId=agenthub --set oidc.audience=agenthub --set ee.admins=team-admin --set ingress.enabled=true --set ingress.host=hub.example.com --set ingress.tlsSecretName=team-proof-tls --set "frontendOrigin=$PROOF_API")
helm upgrade "${args[@]}" --set ingress.className=nginx --wait --timeout 3m
if curl --noproxy '*' --resolve "hub.example.com:443:$node_ip" --cacert "$output/tls.crt" -fsS "$PROOF_API/api/config" >"$output/wrong-class.json"; then
  echo 'Wrong ingress class unexpectedly routed the hub' >&2; exit 1
fi
helm upgrade "${args[@]}" --set ingress.className=traefik --wait --timeout 3m
for i in $(seq 1 60); do
  if curl --noproxy '*' --resolve "hub.example.com:443:$node_ip" --cacert "$output/tls.crt" -fsS "$PROOF_API/api/config" >"$output/config.json"; then break; fi
  sleep 1
done
node -e 'const c=JSON.parse(require("fs").readFileSync(process.argv[1])); if (!JSON.stringify(c).includes(process.env.PROOF_ISSUER)) throw Error("OIDC config missing")' "$output/config.json"
curl --noproxy '*' --resolve "hub.example.com:443:$node_ip" --cacert "$output/tls.crt" -fsS "$PROOF_API/" >"$output/index.html"
grep -q '<html' "$output/index.html"
# Node's API request uses the same ingress hostname as a browser, with no verification bypass.
printf '%s hub.example.com\n' "$node_ip" | sudo tee -a /etc/hosts >/dev/null
NODE_EXTRA_CA_CERTS="$output/tls.crt" node tests/OidcStartupProof/exchange.mjs
helm rollback agenthub "$previous" -n agenthub --wait --timeout 3m
kubectl delete secret team-proof-tls -n agenthub
echo 'PASS: wrong class unreachable; explicit quickstart class serves HTTPS frontend, OIDC config and authenticated admin API'
