#!/usr/bin/env bash
set -euo pipefail
export KUBECONFIG=${KUBECONFIG:-/etc/rancher/k3s/k3s.yaml}
# This workflow uses a disposable real cluster, never a developer's existing installation.
if [ "${GITHUB_EVENT_NAME:-}" != workflow_dispatch ]; then
  echo 'Run only through the explicit disposable-cluster workflow.' >&2
  exit 1
fi
image=example.com/provider-workspace-proof:local
docker build -t "$image" tests/ProviderWorkspaceProof
docker save "$image" -o /tmp/provider-workspace-image.tar
sudo k3s ctr images import /tmp/provider-workspace-image.tar
kubectl -n agenthub set env deployment/agenthub-backend \
  AgentHub__WorkspaceVolumePersistent=true AgentHub__WorkspaceVolumeSize=64Mi \
  AgentHub__ClaudeAgentImage="$image" AgentHub__AgentImagePullPolicy=IfNotPresent
kubectl -n agenthub rollout status deployment/agenthub-backend --timeout=180s
kubectl -n agenthub port-forward service/agenthub-backend 18880:80 >/tmp/provider-workspace-forward.log 2>&1 &
forward_pid=$!
trap 'kill "$forward_pid" 2>/dev/null || true' EXIT
node tests/ProviderWorkspaceProof/prove.mjs
