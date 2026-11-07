#!/usr/bin/env bash
set -euo pipefail
export KUBECONFIG=${KUBECONFIG:-/etc/rancher/k3s/k3s.yaml}
if [ "${GITHUB_EVENT_NAME:-}" != workflow_dispatch ]; then
  echo 'Run through the explicit disposable-cluster provider proof.' >&2
  exit 1
fi
proof_model_url=http://provider-switch-model.agenthub.svc.cluster.local:8080
proof_native_url=https://provider-switch-model.agenthub.svc.cluster.local:8443
proof_context=$(mktemp -d /tmp/provider-switch-images.XXXXXX)
cp tests/ProviderSwitchProof/{Claude.Dockerfile,Codex.Dockerfile,Model.Dockerfile,codex-native.sh,claude-fixture-entrypoint.sh,model.mjs} "$proof_context/"
openssl req -x509 -newkey rsa:2048 -nodes -keyout "$proof_context/ca.key" -out "$proof_context/proof.crt" \
  -days 2 -subj /CN=provider-proof-ca >/dev/null 2>&1
openssl req -newkey rsa:2048 -nodes -keyout "$proof_context/server.key" -out "$proof_context/server.csr" \
  -subj /CN=provider-switch-model.agenthub.svc.cluster.local >/dev/null 2>&1
cat > "$proof_context/leaf.ext" <<'CERT'
subjectAltName=DNS:provider-switch-model.agenthub.svc.cluster.local
basicConstraints=critical,CA:FALSE
keyUsage=critical,digitalSignature,keyEncipherment
extendedKeyUsage=serverAuth
CERT
openssl x509 -req -in "$proof_context/server.csr" -CA "$proof_context/proof.crt" -CAkey "$proof_context/ca.key" \
  -CAcreateserial -out "$proof_context/server.crt" -days 2 -extfile "$proof_context/leaf.ext" >/dev/null 2>&1
for provider in Claude Codex; do
  lower=${provider,,}
  docker build --builder default --build-arg "MODEL_BASE=$proof_native_url" \
    -f "tests/ProviderSwitchProof/$provider.Dockerfile" -t "example.com/provider-switch-$lower:local" "$proof_context"
done
docker build --builder default -f tests/ProviderSwitchProof/Model.Dockerfile \
  -t example.com/provider-switch-model:local "$proof_context"
docker save example.com/provider-switch-claude:local example.com/provider-switch-codex:local \
  example.com/provider-switch-model:local | sudo k3s ctr images import -
kubectl apply -f - <<'YAML'
apiVersion: apps/v1
kind: Deployment
metadata: {name: provider-switch-model, namespace: agenthub}
spec:
  replicas: 1
  selector: {matchLabels: {app: provider-switch-model}}
  template:
    metadata: {labels: {app: provider-switch-model}}
    spec:
      automountServiceAccountToken: false
      securityContext: {runAsNonRoot: true, runAsUser: 1000, runAsGroup: 1000, fsGroup: 1000, seccompProfile: {type: RuntimeDefault}}
      containers:
        - name: model
          image: example.com/provider-switch-model:local
          imagePullPolicy: IfNotPresent
          ports: [{containerPort: 8080}, {containerPort: 8443}]
          securityContext: {readOnlyRootFilesystem: true, allowPrivilegeEscalation: false, capabilities: {drop: [ALL]}}
          resources: {requests: {cpu: 100m, memory: 128Mi}, limits: {cpu: '1', memory: 512Mi}}
          readinessProbe: {tcpSocket: {port: 8080}}
          volumeMounts: [{name: tmp, mountPath: /tmp}]
      volumes: [{name: tmp, emptyDir: {}}]
---
apiVersion: v1
kind: Service
metadata: {name: provider-switch-model, namespace: agenthub}
spec: {selector: {app: provider-switch-model}, ports: [{name: http, port: 8080, targetPort: 8080}, {name: https, port: 8443, targetPort: 8443}]}
---
apiVersion: networking.k8s.io/v1
kind: NetworkPolicy
metadata: {name: provider-switch-model-egress, namespace: agenthub-sessions}
spec:
  podSelector: {matchLabels: {agenthub.dev/component: agent}}
  policyTypes: [Egress]
  egress:
    - to:
        - namespaceSelector: {matchLabels: {kubernetes.io/metadata.name: agenthub}}
          podSelector: {matchLabels: {app: provider-switch-model}}
      ports: [{protocol: TCP, port: 8080}, {protocol: TCP, port: 8443}]
YAML
kubectl -n agenthub rollout status deployment/provider-switch-model --timeout=120s
kubectl -n agenthub set env deployment/agenthub-backend AgentHub__WorkspaceVolumePersistent=true \
  AgentHub__WorkspaceVolumeSize=256Mi AgentHub__ClaudeAgentImage=example.com/provider-switch-claude:local \
  AgentHub__CodexAgentImage=example.com/provider-switch-codex:local AgentHub__AgentImagePullPolicy=IfNotPresent
kubectl -n agenthub rollout status deployment/agenthub-backend --timeout=180s
kubectl -n agenthub port-forward service/agenthub-backend 18880:80 >/tmp/provider-switch-forward.log 2>&1 &
forward_pid=$!
cleanup() {
  kill "$forward_pid" 2>/dev/null || true
  kubectl -n agenthub delete deployment/provider-switch-model service/provider-switch-model --ignore-not-found --wait=false
  kubectl -n agenthub-sessions delete networkpolicy/provider-switch-model-egress --ignore-not-found
}
trap cleanup EXIT
npm install --prefix /tmp/provider-switch-tools ws@8.22.0 @xterm/headless@6.0.0 >/tmp/provider-switch-tools.log 2>&1
PROOF_MODEL_URL="$proof_model_url" node tests/ProviderSwitchProof/prove.mjs
