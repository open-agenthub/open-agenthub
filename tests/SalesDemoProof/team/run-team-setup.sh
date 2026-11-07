#!/usr/bin/env bash
# Timed team setup on the quickstart guest, following the published getting-started steps:
# certificate Secret, team-values.yaml, then the documented helm upgrade. Every output line is
# timestamped into ~/team.log in the guest. Extra flags (a fix found on the way) go in $EXTRA.
set -u
cd "$(dirname "$0")/../../SalesPathAuditProof"
scp -q -P 2222 -i "${TASK_ROOT:-$HOME/rig/sales-audit-vm}/private/demo-ssh" \
  -o UserKnownHostsFile="${TASK_ROOT:-$HOME/rig/sales-audit-vm}/private/known_hosts" \
  ../SalesDemoProof/team/team-values.yaml demo@127.0.0.1:
bash ssh.sh "EXTRA='${EXTRA:-}' bash -s" <<'GUEST'
export KUBECONFIG=/etc/rancher/k3s/k3s.yaml
ts() { while IFS= read -r l; do printf '%s %s\n' "$(date -u +%T)" "$l"; done; }
{
  date -u +START\ %FT%TZ
  kubectl -n agenthub get secret hub-tls >/dev/null 2>&1 || \
    kubectl -n agenthub create secret tls hub-tls --cert=hub.example.com.crt --key=hub.example.com.key
  time helm upgrade agenthub agenthub/open-agenthub -n agenthub --reset-then-reuse-values \
    -f team-values.yaml $EXTRA --wait --timeout 5m
  echo EXIT=$?
  kubectl -n agenthub get ingress,pods
} 2>&1 | ts >> ~/team.log
tail -30 ~/team.log
GUEST
