#!/usr/bin/env bash
set -euo pipefail
# A healthy Helm release must also have initialized without restarting either container.
kubectl -n agenthub get pod postgres-0 -o json
kubectl -n agenthub logs postgres-0 -c initialize-database --timestamps
kubectl -n agenthub logs postgres-0 -c postgres --timestamps
kubectl -n agenthub get pod postgres-0 -o json | jq -e '
  ([.status.initContainerStatuses[], .status.containerStatuses[]] | all(.restartCount == 0)) and
  (.status.initContainerStatuses[] | select(.name == "initialize-database") | .state.terminated.exitCode == 0)'
sudo journalctl -k --no-pager | tail -100
for attempt in $(seq 1 5); do
  name="postgres-startup-$attempt"
  kubectl create namespace "$name"
  helm template agenthub helm/open-agenthub --namespace "$name" \
    --set postgres.password=proof --set postgres.persistence=false \
    --show-only templates/postgres.yaml | kubectl -n "$name" apply -f -
  if ! kubectl -n "$name" rollout status statefulset/postgres --timeout=120s; then
    kubectl -n "$name" get pods -o json
    kubectl -n "$name" logs postgres-0 -c initialize-database --timestamps || true
    kubectl -n "$name" logs postgres-0 -c postgres --timestamps || true
    exit 1
  fi
  kubectl -n "$name" get pod postgres-0 -o json | jq -e '
    [.status.initContainerStatuses[], .status.containerStatuses[]] | all(.restartCount == 0)'
  kubectl -n "$name" logs postgres-0 -c initialize-database --timestamps
  kubectl -n "$name" logs postgres-0 -c postgres --timestamps
  kubectl -n "$name" exec postgres-0 -c postgres -- \
    psql -U agenthub -d agenthub -v ON_ERROR_STOP=1 -c 'CHECKPOINT'
  # Prove the generated password/HBA setup over TCP, not only trusted local sockets.
  kubectl -n "$name" exec postgres-0 -c postgres -- bash -ec \
    'PGPASSWORD="$POSTGRES_PASSWORD" psql -h 127.0.0.1 -U agenthub -d agenthub -v ON_ERROR_STOP=1 -c "SELECT current_user, current_database()"'
  kubectl delete namespace "$name" --wait=false
  echo "Single-user chart startup $attempt: zero restarts, TCP authentication and CHECKPOINT succeeded"
done
