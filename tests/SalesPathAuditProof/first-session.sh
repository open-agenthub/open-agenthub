#!/usr/bin/env bash
# In the guest: create the prospect's first task as an Autonomous Claude session on the stand-in
# model endpoint and repository, then follow the pod until it ends. Uses the same REST call the UI
# makes, authenticated with the generated API key, which never leaves the guest.
set -u
cd "$(dirname "$0")"
bash ssh.sh 'export KUBECONFIG=/etc/rancher/k3s/k3s.yaml
KEY=$(kubectl -n agenthub get secret agenthub-api-key -o jsonpath="{.data.api-key}" | base64 -d)
date -u +"CREATE %T"
curl -s -X POST http://localhost:8080/api/sessions -H "Authorization: Bearer $KEY" -H "content-type: application/json" -d "{
  \"title\":\"First task: README usage section\",\"mode\":\"Autonomous\",\"agent\":\"Claude\",\"authMode\":\"CustomEndpoint\",
  \"modelEndpointUrl\":\"http://model.audit.svc.cluster.local:443\",\"modelName\":\"audit-model\",
  \"repos\":[{\"url\":\"git://gitd.audit.svc.cluster.local:443/demo.git\"}],
  \"prompt\":\"AUDIT-TASK: add a Usage section to the README, push a branch and open a pull request.\"}" | tee /tmp/create.json | head -c 600; echo'
