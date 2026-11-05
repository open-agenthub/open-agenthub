#!/usr/bin/env bash
# Temporary credentials stay outside Git; the certificate is trusted only by proof processes.
set -euo pipefail
proof_dir=${1:?temporary output directory}
mkdir -p "$proof_dir/certs"
openssl req -x509 -newkey rsa:2048 -nodes -keyout "$proof_dir/certs/server.key" \
  -out "$proof_dir/certs/server.crt" -days 2 -subj /CN=127.0.0.1 \
  -addext 'subjectAltName=IP:127.0.0.1,DNS:localhost' >/dev/null 2>&1
cat /etc/ssl/certs/ca-certificates.crt "$proof_dir/certs/server.crt" > "$proof_dir/certs/bundle.crt"
cat > "$proof_dir/kubeconfig" <<'CONFIG'
apiVersion: v1
kind: Config
clusters:
- name: proof
  cluster:
    server: http://127.0.0.1:18081
contexts:
- name: proof
  context:
    cluster: proof
    user: proof
current-context: proof
users:
- name: proof
  user:
    token: proof-not-a-secret
CONFIG
