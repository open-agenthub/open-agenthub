#!/usr/bin/env bash
set -euo pipefail
release_namespace=${RELEASE_NAMESPACE:-agenthub}
sessions_namespace=${SESSIONS_NAMESPACE:-agenthub-sessions}
probe="storage-egress-proof-${RANDOM}"
cleanup() {
  kubectl -n "$sessions_namespace" delete pod "$probe" --ignore-not-found --wait=false
  kubectl -n "$release_namespace" delete pod "$probe" "${probe}-control" --ignore-not-found --wait=false
}
trap cleanup EXIT
kubectl -n "$release_namespace" get pods -l app=garage
kubectl -n "$sessions_namespace" get networkpolicy allow-agent-to-object-storage -o yaml
# A listening unrelated destination and an unrestricted control prevent a false negative
# caused by an unavailable target rather than actual NetworkPolicy enforcement.
kubectl -n "$release_namespace" run "$probe" --image=busybox:1.37 --restart=Never --command -- httpd -f -p 3900
kubectl -n "$release_namespace" run "${probe}-control" --image=busybox:1.37 --restart=Never --command -- sleep 600
kubectl -n "$sessions_namespace" run "$probe" --image=busybox:1.37 --restart=Never \
  --labels="agenthub.dev/component=agent,agenthub.dev/session=$probe" --command -- sleep 600
for pair in "$release_namespace/$probe" "$release_namespace/${probe}-control" "$sessions_namespace/$probe"; do
  kubectl -n "${pair%/*}" wait --for=condition=Ready "pod/${pair#*/}" --timeout=120s
done
target_ip=$(kubectl -n "$release_namespace" get pod "$probe" -o jsonpath='{.status.podIP}')
kubectl -n "$release_namespace" exec "${probe}-control" -- nc -z -w 5 "$target_ip" 3900
# Allow the policy controller to reconcile the freshly labelled probe before measuring.
sleep 10
kubectl -n "$sessions_namespace" exec "$probe" -- nc -z -w 5 "garage.$release_namespace.svc.cluster.local" 3900
if kubectl -n "$sessions_namespace" exec "$probe" -- nc -z -w 5 "$target_ip" 3900; then
  echo 'FAIL: unrelated TCP 3900 destination reachable; storage policy is not isolated' >&2
  exit 1
fi
echo 'PASS: session-labelled pod reaches Garage:3900; unrelated live TCP 3900 target blocked'
