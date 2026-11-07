#!/usr/bin/env bash
# OFF-DOCS WORKAROUND, rig only (#481 setup measurement): the chart has no value that adds a CA to
# the backend's trust store (agent.trustedCaConfigMap covers session pods only), so a backend
# cannot validate an issuer whose certificate comes from a private CA, and its OIDC start-up check
# stops it. This mounts the guest's CA bundle (which includes the stand-in CA) and points
# OpenSSL's SSL_CERT_FILE at it. A `helm upgrade` may drop it again; re-run this afterwards.
set -euo pipefail
cd "$(dirname "$0")/../../SalesPathAuditProof"
bash ssh.sh 'bash -s' <<'GUEST'
set -e; export KUBECONFIG=/etc/rancher/k3s/k3s.yaml
kubectl -n agenthub create configmap demo-ca-bundle --from-file=ca-bundle.crt=/etc/ssl/certs/ca-certificates.crt \
  --dry-run=client -o yaml | kubectl apply -f - >/dev/null
kubectl -n agenthub patch deployment agenthub-backend --type=strategic -p '{"spec":{"template":{"spec":{
  "volumes":[{"name":"demo-ca","configMap":{"name":"demo-ca-bundle"}}],
  "containers":[{"name":"backend","env":[{"name":"SSL_CERT_FILE","value":"/etc/demo-ca/ca-bundle.crt"}],
    "volumeMounts":[{"name":"demo-ca","mountPath":"/etc/demo-ca","readOnly":true}]}]}}}}'
kubectl -n agenthub rollout status deploy/agenthub-backend --timeout=6m
GUEST
