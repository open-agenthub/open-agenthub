#!/usr/bin/env bash
# Stand-in for the team's DNS: hub.example.com and id.example.com point at the node's ingress, for
# the guest (/etc/hosts) and for pods (k3s CoreDNS reads *.override from coredns-custom). A real
# team has public or internal DNS records instead; this is rig plumbing, not a product step.
set -euo pipefail
cd "$(dirname "$0")/../../SalesPathAuditProof"
bash ssh.sh 'bash -s' <<'GUEST'
set -e; export KUBECONFIG=/etc/rancher/k3s/k3s.yaml
IP=$(kubectl -n kube-system get svc traefik -o jsonpath='{.status.loadBalancer.ingress[0].ip}')
grep -q hub.example.com /etc/hosts || echo "$IP hub.example.com id.example.com" | sudo tee -a /etc/hosts >/dev/null
kubectl apply -f - <<YAML
apiVersion: v1
kind: ConfigMap
metadata: { name: coredns-custom, namespace: kube-system }
data:
  demo.override: |
    hosts {
      $IP hub.example.com id.example.com
      fallthrough
    }
YAML
kubectl -n kube-system rollout restart deploy/coredns >/dev/null; echo "names -> $IP"
GUEST
