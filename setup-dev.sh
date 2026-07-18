#!/usr/bin/env bash
set -Eeuo pipefail

release_name='agenthub-dev'
control_namespace='agenthub-dev'
sessions_namespace='agenthub-dev-sessions'
required_context='docker-desktop'
no_port_forward=false

if [[ $# -gt 1 || ( $# -eq 1 && "$1" != '--no-port-forward' ) ]]; then
  printf 'Usage: %s [--no-port-forward]\n' "$0" >&2
  exit 2
fi
if [[ $# -eq 1 ]]; then
  no_port_forward=true
fi

require_command() {
  command -v "$1" >/dev/null 2>&1 || {
    printf 'Required command not found: %s\n' "$1" >&2
    exit 1
  }
}

for command in docker kubectl helm curl base64; do
  require_command "$command"
done

current_context="$(kubectl config current-context 2>/dev/null || true)"
if [[ "$current_context" != "$required_context" ]]; then
  printf "Refusing to deploy: kubectl context '%s' is not '%s'.\n" "$current_context" "$required_context" >&2
  exit 1
fi

script_dir="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd -P)"
chart_path="$script_dir/helm/open-agenthub"
values_path="$chart_path/values-dev.yaml"
# Optional, gitignored personal overrides (git OAuth apps, Slack tokens, …).
local_values_path="$chart_path/values-dev.local.yaml"
if [[ ! -f "$values_path" ]]; then
  printf 'Development values file not found: %s\n' "$values_path" >&2
  exit 1
fi

printf 'Building local images...\n'
docker build --file "$script_dir/backend/Dockerfile" --tag 'open-agenthub-dev/backend:local' "$script_dir"
docker build --tag 'open-agenthub-dev/frontend:local' "$script_dir/frontend"
docker build --tag 'open-agenthub-dev/agent-runtime:local' "$script_dir/agent-runtime"

decode_base64() {
  if printf '' | base64 --decode >/dev/null 2>&1; then
    printf '%s' "$1" | base64 --decode
  else
    printf '%s' "$1" | base64 -D
  fi
}

encoded_password=''
if encoded_password="$(kubectl -n "$control_namespace" get secret postgres-secret -o 'jsonpath={.data.password}' 2>/dev/null)" &&
  [[ -n "$encoded_password" ]]; then
  if ! postgres_password="$(decode_base64 "$encoded_password")" || [[ -z "$postgres_password" ]]; then
    printf 'The existing postgres-secret contains an invalid password value.\n' >&2
    exit 1
  fi
else
  if helm status "$release_name" --namespace "$control_namespace" >/dev/null 2>&1; then
    printf 'The existing Helm release is missing postgres-secret; refusing to rotate the database password.\n' >&2
    exit 1
  fi
  postgres_password="$(head -c 32 /dev/urandom | od -An -tx1 | tr -d ' \n')"
fi
backend_forward_pid=''
frontend_forward_pid=''

cleanup() {
  if [[ -n "$backend_forward_pid" ]] && kill -0 "$backend_forward_pid" 2>/dev/null; then
    kill "$backend_forward_pid" 2>/dev/null || true
    wait "$backend_forward_pid" 2>/dev/null || true
  fi
  if [[ -n "$frontend_forward_pid" ]] && kill -0 "$frontend_forward_pid" 2>/dev/null; then
    kill "$frontend_forward_pid" 2>/dev/null || true
    wait "$frontend_forward_pid" 2>/dev/null || true
  fi
}
trap cleanup EXIT INT TERM

printf 'Deploying the development release...\n'
helm_values=(--values "$values_path")
if [[ -f "$local_values_path" ]]; then
  printf 'Applying local overrides from %s\n' "$local_values_path"
  helm_values+=(--values "$local_values_path")
fi
helm upgrade --install "$release_name" "$chart_path" \
  --namespace "$control_namespace" \
  --create-namespace \
  "${helm_values[@]}" \
  --set "sessionsNamespace=$sessions_namespace" \
  --set-string "postgres.password=$postgres_password"

kubectl -n "$control_namespace" rollout status statefulset/postgres --timeout=180s
kubectl -n "$control_namespace" rollout restart deployment/agenthub-backend deployment/agenthub-frontend
kubectl -n "$control_namespace" rollout status deployment/agenthub-backend --timeout=180s
kubectl -n "$control_namespace" rollout status deployment/agenthub-frontend --timeout=180s

wait_for_url() {
  local url="$1"
  for _ in {1..30}; do
    if curl --fail --silent --show-error --max-time 2 "$url" >/dev/null; then
      return 0
    fi
    sleep 1
  done
  printf 'Health check failed: %s\n' "$url" >&2
  return 1
}

kubectl -n "$control_namespace" port-forward svc/agenthub-backend 18080:80 >/tmp/agenthub-dev-backend-port-forward.log 2>&1 &
backend_forward_pid=$!
wait_for_url 'http://127.0.0.1:18080/healthz'
kill "$backend_forward_pid" 2>/dev/null || true
wait "$backend_forward_pid" 2>/dev/null || true
backend_forward_pid=''

kubectl -n "$control_namespace" port-forward svc/agenthub-frontend 18081:80 >/tmp/agenthub-dev-frontend-port-forward.log 2>&1 &
frontend_forward_pid=$!
wait_for_url 'http://127.0.0.1:18081/'
kill "$frontend_forward_pid" 2>/dev/null || true
wait "$frontend_forward_pid" 2>/dev/null || true
frontend_forward_pid=''

unset postgres_password
printf 'Development release is ready.\n'
printf 'Control namespace: agenthub-dev\n'
printf 'Sessions namespace: agenthub-dev-sessions\n'
printf '  Logs: kubectl -n agenthub-dev logs deployment/agenthub-backend --follow\n'
printf '  Redeploy: ./setup-dev.sh --no-port-forward\n'
printf '  Uninstall: helm uninstall agenthub-dev -n agenthub-dev\n'
printf '  Remove sessions: kubectl delete namespace agenthub-dev-sessions\n'

if [[ "$no_port_forward" == true ]]; then
  printf 'Port-forward skipped (--no-port-forward).\n'
  printf 'Run: kubectl -n agenthub-dev port-forward svc/agenthub-frontend 8080:80\n'
else
  printf 'Serving the frontend at http://localhost:8080. Press Ctrl+C to stop.\n'
  kubectl -n "$control_namespace" port-forward svc/agenthub-frontend 8080:80
fi
