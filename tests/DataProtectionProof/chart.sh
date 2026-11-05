#!/usr/bin/env bash
set -euo pipefail
# A real API server is needed: offline template rendering cannot exercise lookup.
: "${KUBECONFIG:?set KUBECONFIG to an isolated disposable API server}"
chart_helm=${HELM:-helm}
chart_kubectl=${KUBECTL:-kubectl}
chart_repo=$(cd "$(dirname "$0")/../.." && pwd)
chart_namespace=protection-proof
chart_release=protection-proof
chart_tmp=$(mktemp -d)
trap 'rm -rf "$chart_tmp"' EXIT
"$chart_kubectl" create namespace "$chart_namespace" --dry-run=client -o yaml | "$chart_kubectl" apply -f -
"$chart_helm" install "$chart_release" "$chart_repo/helm/open-agenthub" -n "$chart_namespace" --set postgres.password=synthetic-proof-password > "$chart_tmp/install.txt"
"$chart_kubectl" get secret agenthub-data-protection -n "$chart_namespace" -o jsonpath='{.data}' > "$chart_tmp/before.json"
"$chart_helm" upgrade "$chart_release" "$chart_repo/helm/open-agenthub" -n "$chart_namespace" --set postgres.password=synthetic-proof-password > "$chart_tmp/upgrade.txt"
"$chart_kubectl" get secret agenthub-data-protection -n "$chart_namespace" -o jsonpath='{.data}' > "$chart_tmp/after.json"
cmp "$chart_tmp/before.json" "$chart_tmp/after.json"
echo 'PASS: real Helm install/upgrade retains the exact wrapping certificate and private key'
"$chart_helm" uninstall "$chart_release" -n "$chart_namespace" > "$chart_tmp/uninstall.txt"
"$chart_kubectl" get secret agenthub-data-protection -n "$chart_namespace" -o jsonpath='{.data}' > "$chart_tmp/retained.json"
cmp "$chart_tmp/before.json" "$chart_tmp/retained.json"
"$chart_kubectl" wait --for=delete namespace/agenthub-sessions --timeout=90s
"$chart_helm" install "$chart_release" "$chart_repo/helm/open-agenthub" -n "$chart_namespace" --set postgres.password=synthetic-proof-password > "$chart_tmp/reinstall.txt"
"$chart_kubectl" get secret agenthub-data-protection -n "$chart_namespace" -o jsonpath='{.data}' > "$chart_tmp/reinstalled.json"
cmp "$chart_tmp/before.json" "$chart_tmp/reinstalled.json"
echo 'PASS: uninstall retains the Secret and reinstall reuses it'
"$chart_helm" template "$chart_release" "$chart_repo/helm/open-agenthub" --set postgres.password=synthetic-proof-password --set dataProtection.existingSecret=operator-ring > "$chart_tmp/existing.yaml"
if grep -q 'name: agenthub-data-protection' "$chart_tmp/existing.yaml"; then echo 'unexpected generated Secret' >&2; exit 1; fi
grep -q 'name: "operator-ring"' "$chart_tmp/existing.yaml"
echo 'PASS: existingSecret references the operator identity and emits no generated wrapping Secret'
