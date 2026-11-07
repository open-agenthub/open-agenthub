#!/usr/bin/env bash
set -euo pipefail
proof_dir=$(cd "$(dirname "$0")" && pwd)
output=$(mktemp -d)
previous=$(helm history agenthub -n agenthub -o json | jq -r '[.[] | select(.status == "deployed")][-1].revision')
node_ip=$(kubectl get nodes -o jsonpath='{.items[0].status.addresses[?(@.type=="InternalIP")].address}')
export PROOF_ISSUER="https://$node_ip:18081"
export PROOF_TLS_CERT="$output/issuer.crt" PROOF_TLS_KEY="$output/issuer.key"
openssl req -x509 -newkey rsa:2048 -nodes -keyout "$output/ca.key" -out "$output/ca.crt" -days 1 -subj '/CN=Install proof CA'
openssl req -newkey rsa:2048 -nodes -keyout "$PROOF_TLS_KEY" -out "$output/issuer.csr" -subj '/CN=identity.example.com'
printf 'subjectAltName=IP:%s\nextendedKeyUsage=serverAuth\n' "$node_ip" >"$output/extensions"
openssl x509 -req -in "$output/issuer.csr" -CA "$output/ca.crt" -CAkey "$output/ca.key" -CAcreateserial -out "$PROOF_TLS_CERT" -days 1 -extfile "$output/extensions"
node "$proof_dir/issuer.mjs" >"$output/issuer.log" 2>&1 &
issuer_pid=$!
cleanup() { kill "$issuer_pid" 2>/dev/null || true; if [ -n "${proxy_pid:-}" ]; then kill "$proxy_pid" 2>/dev/null || true; fi; }
trap cleanup EXIT
for i in $(seq 1 30); do if curl --cacert "$output/ca.crt" -fsS "$PROOF_ISSUER/.well-known/openid-configuration" >/dev/null; then break; fi; sleep 1; done
kubectl create configmap backend-proof-ca -n agenthub --from-file="ca.crt=$output/ca.crt" --dry-run=client -o yaml | kubectl apply -f -
args=(agenthub ./helm/open-agenthub -n agenthub --reuse-values --set "oidc.authority=$PROOF_ISSUER" --set oidc.requireHttpsMetadata=true --set oidc.clientId=agenthub --set oidc.audience=agenthub --set ee.admins=team-admin)
if helm upgrade "${args[@]}" --set backend.trustedCaConfigMap= --wait --timeout 90s >"$output/untrusted-upgrade.log" 2>&1; then
  echo 'Private issuer unexpectedly trusted without the bundle' >&2; exit 1
fi
kubectl logs -n agenthub -l app=agenthub-backend --all-containers --tail=50 --prefix >"$output/untrusted-pods.log" || true
grep -q 'FATAL: OIDC startup validation failed' "$output/untrusted-pods.log"
grep -Ei 'certificate|SSL|TLS|UntrustedRoot' "$output/untrusted-pods.log"
helm rollback agenthub "$previous" -n agenthub --wait --timeout 3m
helm upgrade "${args[@]}" --set backend.trustedCaConfigMap=backend-proof-ca --wait --timeout 3m
kubectl port-forward -n agenthub svc/agenthub-frontend 18080:80 >"$output/proxy.log" 2>&1 &
proxy_pid=$!
for i in $(seq 1 30); do if curl -fsS http://127.0.0.1:18080/api/config >/dev/null; then break; fi; sleep 1; done
NODE_EXTRA_CA_CERTS="$output/ca.crt" node "$proof_dir/exchange.mjs"
helm rollback agenthub "$previous" -n agenthub --wait --timeout 3m
kubectl delete configmap backend-proof-ca -n agenthub
echo 'PASS: private CA fails without bundle, succeeds with read-only backend bundle; quickstart restored'
