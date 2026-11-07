#!/usr/bin/env bash
set -euo pipefail
namespace=agenthub
release=agenthub
proof_dir=$(cd "$(dirname "$0")" && pwd)
output=$(mktemp -d)
node_ip=$(kubectl get nodes -o jsonpath='{.items[0].status.addresses[?(@.type=="InternalIP")].address}')
export PROOF_ISSUER="http://$node_ip:18081"
node "$proof_dir/issuer.mjs" >"$output/issuer.log" 2>&1 &
issuer_pid=$!
cleanup() { kill "$issuer_pid" 2>/dev/null || true; if [ -n "${proxy_pid:-}" ]; then kill "$proxy_pid" 2>/dev/null || true; fi; }
trap cleanup EXIT
for i in $(seq 1 30); do if curl -fsS "$PROOF_ISSUER/.well-known/openid-configuration" > /dev/null; then break; fi; sleep 1; done
previous=$(helm history "$release" -n "$namespace" -o json | jq -r '[.[] | select(.status == "deployed")][-1].revision')

# Use the real quickstart release and the checkout image for both failures. Helm's failed
# upgrade is rolled back before the next case, exercising the documented recovery path.
for failure in https unreachable; do
  authority=$PROOF_ISSUER
  policy=true
  if [ "$failure" = unreachable ]; then authority="http://$node_ip:18082"; policy=false; fi
  if helm upgrade "$release" ./helm/open-agenthub -n "$namespace" --reuse-values \
      --set "oidc.authority=$authority" --set "oidc.requireHttpsMetadata=$policy" \
      --wait --timeout 90s >"$output/$failure-upgrade.log" 2>&1; then
    echo "Unusable issuer unexpectedly became ready" >&2; exit 1
  fi
  kubectl logs -n "$namespace" -l app=agenthub-backend --all-containers --tail=30 --prefix >"$output/$failure-pods.log" || true
  grep -q 'FATAL: OIDC startup validation failed' "$output/$failure-pods.log"
  helm rollback "$release" "$previous" -n "$namespace" --wait --timeout 3m
  echo "PASS: $failure issuer refused startup; previous quickstart revision restored"
done
helm upgrade "$release" ./helm/open-agenthub -n "$namespace" --reuse-values \
  --set "oidc.authority=$PROOF_ISSUER" --set oidc.requireHttpsMetadata=false \
  --set oidc.clientId=agenthub --set oidc.audience=agenthub --set ee.admins=team-admin \
  --wait --timeout 3m
kubectl port-forward -n "$namespace" svc/agenthub-frontend 18080:80 >"$output/proxy.log" 2>&1 &
proxy_pid=$!
for i in $(seq 1 30); do if curl -fsS http://127.0.0.1:18080/api/config >"$output/config.json"; then break; fi; sleep 1; done
node "$proof_dir/exchange.mjs"
helm rollback "$release" "$previous" -n "$namespace" --wait --timeout 3m
echo 'PASS: quickstart restored after OIDC install proof'
