#!/usr/bin/env bash
set -Eeuo pipefail

release_name='agenthub-dev'
control_namespace='agenthub-dev'
sessions_namespace='agenthub-dev-sessions'
required_context='docker-desktop'
no_port_forward=false
object_storage=''

while [[ $# -gt 0 ]]; do
  case "$1" in
    --no-port-forward) no_port_forward=true ;;
    --with-object-storage) object_storage=true ;;
    --without-object-storage) object_storage=false ;;
    *)
      printf 'Usage: %s [--no-port-forward] [--with-object-storage|--without-object-storage]\n' "$0" >&2
      exit 2
      ;;
  esac
  shift
done

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
docker build --file "$script_dir/agent-runtime/claude/Dockerfile" --tag 'open-agenthub-dev/agent-runtime-claude:local' "$script_dir/agent-runtime"
docker build --file "$script_dir/agent-runtime/codex/Dockerfile" --tag 'open-agenthub-dev/agent-runtime-codex:local' "$script_dir/agent-runtime"
docker build --file "$script_dir/agent-runtime/cursor/Dockerfile" --tag 'open-agenthub-dev/agent-runtime-cursor:local' "$script_dir/agent-runtime"
docker build --file "$script_dir/agent-runtime/openclaw/Dockerfile" --tag 'open-agenthub-dev/agent-runtime-openclaw:local' "$script_dir/agent-runtime"
docker build --tag 'open-agenthub-dev/browser:local' "$script_dir/browser-runtime"

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
random_hex() {
  head -c "$1" /dev/urandom | od -An -tx1 | tr -d ' \n'
}

# Reads a value out of an existing secret; empty when the secret, the key, or the value
# itself is absent. Credentials are never regenerated on a redeploy: a new access key would
# leave every object already in the bucket unreachable, and the hub would report that as
# missing session state rather than as a credential it no longer has.
existing_secret_value() {
  local secret="$1" key="$2" encoded
  encoded="$(kubectl -n "$control_namespace" get secret "$secret" -o "jsonpath={.data.$key}" 2>/dev/null || true)"
  [[ -n "$encoded" ]] || return 0
  decode_base64 "$encoded"
}

# Object storage: offered rather than assumed. Without it the hub runs, but a session
# cannot be resumed in a fresh pod — its state archive has nowhere to live.
if [[ -z "$object_storage" ]]; then
  if kubectl -n "$control_namespace" get statefulset garage >/dev/null 2>&1; then
    object_storage=true
  elif [[ -t 0 ]]; then
    printf 'Deploy object storage (Garage) into the cluster as well?\n'
    printf 'Without it, session state, uploads and artifacts have nowhere to be stored.\n'
    read -r -p 'Deploy it? [Y/n] ' answer
    case "${answer:-y}" in
      [nN]*) object_storage=false ;;
      *)     object_storage=true ;;
    esac
  else
    printf 'Object storage not requested; pass --with-object-storage to deploy it.\n'
    object_storage=false
  fi
fi

object_storage_values=()
if [[ "$object_storage" == true ]]; then
  garage_access_key="$(existing_secret_value agenthub-secrets S3__AccessKey)"
  garage_secret_key="$(existing_secret_value agenthub-secrets S3__SecretKey)"
  garage_rpc_secret="$(existing_secret_value garage-secrets rpc_secret)"
  garage_admin_token="$(existing_secret_value garage-secrets admin_token)"
  # Garage only accepts an access key id shaped like its own: GK plus 24 hex characters.
  [[ -n "$garage_access_key" ]] || garage_access_key="GK$(random_hex 12)"
  [[ -n "$garage_secret_key" ]] || garage_secret_key="$(random_hex 32)"
  [[ -n "$garage_rpc_secret" ]] || garage_rpc_secret="$(random_hex 32)"
  [[ -n "$garage_admin_token" ]] || garage_admin_token="$(random_hex 16)"
  object_storage_values=(
    --set 'objectStorage.enabled=true'
    --set-string "objectStorage.accessKey=$garage_access_key"
    --set-string "objectStorage.secretKey=$garage_secret_key"
    --set-string "objectStorage.rpcSecret=$garage_rpc_secret"
    --set-string "objectStorage.adminToken=$garage_admin_token"
  )
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
  "${object_storage_values[@]+"${object_storage_values[@]}"}" \
  --set "sessionsNamespace=$sessions_namespace" \
  --set-string "postgres.password=$postgres_password"

kubectl -n "$control_namespace" rollout status statefulset/postgres --timeout=180s

# Garage creates nothing by itself: a fresh node has no layout, no bucket and no key, and
# its image has no shell for a bootstrap job to use. Each step below is skipped when it is
# already done, so a redeploy costs nothing.
if [[ "$object_storage" == true ]]; then
  kubectl -n "$control_namespace" rollout status statefulset/garage --timeout=180s
  garage() { kubectl -n "$control_namespace" exec garage-0 -- /garage "$@"; }

  if garage bucket list 2>/dev/null | grep -qE "[[:space:]]agenthub[[:space:]]"; then
    printf 'Object storage already initialised.\n'
  else
    printf 'Initialising object storage...\n'
    layout="$(garage layout show 2>/dev/null || true)"
    current_version="$(printf '%s' "$layout" | sed -n 's/.*Current cluster layout version: \([0-9]*\).*/\1/p' | tail -1)"
    if [[ "${current_version:-0}" -lt 1 ]]; then
      node_id="$(garage node id -q 2>/dev/null | tr -d '\r' | cut -d@ -f1)"
      if [[ -z "$node_id" ]]; then
        printf 'Could not read the Garage node id; object storage is not initialised.\n' >&2
        exit 1
      fi
      garage layout assign -z dc1 -c 18GB "$node_id"
      # The version to apply is always one past the current one; parsing it out of the
      # hint Garage prints would tie this to that sentence's wording.
      garage layout apply --version "$(( ${current_version:-0} + 1 ))"
    fi
    garage bucket create agenthub
    garage key import --yes "$garage_access_key" "$garage_secret_key" -n agenthub-key
    garage bucket allow --read --write --owner agenthub --key agenthub-key
    printf 'Object storage ready: bucket agenthub on garage.%s.svc.cluster.local:3900\n' "$control_namespace"
  fi
  unset -f garage
fi
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
