#!/usr/bin/env bash
set -euo pipefail
# Keep failures' termination state and init-server output: current logs lose both on restart.
kubectl -n agenthub get pod postgres-0 -o json
kubectl -n agenthub logs postgres-0 --timestamps
kubectl -n agenthub logs postgres-0 --previous --timestamps || true
sudo journalctl -k --no-pager | tail -200
sudo sysctl kernel.apparmor_restrict_unprivileged_unconfined || true
sudo aa-status || true
for attempt in $(seq 1 15); do
  name="postgres-startup-$attempt"
  kubectl create namespace "$name"
  cat <<YAML | kubectl -n "$name" apply -f -
apiVersion: v1
kind: Pod
metadata:
  name: postgres
spec:
  securityContext: { fsGroup: 999, runAsNonRoot: true, runAsUser: 999 }
  containers:
    - name: postgres
      image: postgres:16-alpine
      env:
        - { name: POSTGRES_PASSWORD, value: proof }
        - { name: POSTGRES_DB, value: agenthub }
        - { name: POSTGRES_USER, value: agenthub }
        - { name: PGDATA, value: /var/lib/postgresql/data/pgdata }
      volumeMounts: [{ name: data, mountPath: /var/lib/postgresql/data }]
  volumes:
    - name: data
      emptyDir: {}
YAML
  ok=false
  for poll in $(seq 1 45); do
    if kubectl -n "$name" exec postgres -- pg_isready -h 127.0.0.1 -U agenthub -d agenthub; then
      ok=true
      break
    fi
    sleep 2
  done
  kubectl -n "$name" get pod postgres -o json
  kubectl -n "$name" logs postgres --timestamps
  kubectl -n "$name" logs postgres --previous --timestamps || true
  kubectl -n "$name" exec postgres -- sh -c 'id; cat /proc/1/attr/current; ls -ldn /var/run/postgresql; sleep 10 & child=$!; kill -INT "$child"; kill -TERM "$child"' || true
  sudo journalctl -k --no-pager | tail -100
  test "$ok" = true
  test "$(kubectl -n "$name" get pod postgres -o jsonpath='{.status.containerStatuses[0].restartCount}')" = 0
  kubectl -n "$name" exec postgres -- psql -U agenthub -d agenthub -v ON_ERROR_STOP=1 -c 'CHECKPOINT'
  kubectl delete namespace "$name" --wait=false
  echo "First startup $attempt: TCP ready, zero restarts, CHECKPOINT succeeded"
done
