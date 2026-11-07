#!/usr/bin/env bash
# Starts the real backend build (with the test public key patched in, see README) against a
# local Postgres, API-key login and the local licence service. No Kubernetes: the admin and
# licence endpoints do not need it.
#   BACKEND_BIN=<publish dir> PG=<connection string> ./start-backend.sh
set -euo pipefail
# The backend reads ~/.kube/config at start; a private HOME with a kubeconfig pointing nowhere
# satisfies it without a cluster (and never touches a real one).
RIG=${RIG:-$HOME/rig/lic}
mkdir -p "$RIG/home/.kube"; [ -f "$RIG/home/.kube/config" ] || printf "%s\n" "apiVersion: v1" "kind: Config" "clusters: [{name: none, cluster: {server: \"https://127.0.0.1:1\", insecure-skip-tls-verify: true}}]" "users: [{name: none, user: {token: none}}]" "contexts: [{name: none, context: {cluster: none, user: none}}]" "current-context: none" > "$RIG/home/.kube/config"
export PATH=$HOME/.dotnet:$PATH DOTNET_ROOT=$HOME/.dotnet DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=1
[ -f "$RIG/api.key" ] || { openssl rand -hex 24 > "$RIG/api.key"; chmod 600 "$RIG/api.key"; }
[ -f "$RIG/dp.key" ] || { openssl req -x509 -newkey rsa:2048 -nodes -keyout "$RIG/dp.key" -out "$RIG/dp.crt" -days 30 -subj /CN=audit 2>/dev/null; chmod 600 "$RIG/dp.key"; }
ASPNETCORE_URLS=http://127.0.0.1:${BACKEND_PORT:-18090} ASPNETCORE_ENVIRONMENT=Production \
ConnectionStrings__Postgres="${PG:-Host=127.0.0.1;Port=54329;Username=postgres;Database=agenthub_lic}" \
DataProtection__CertificatePem="$(cat "$RIG/dp.crt")" DataProtection__PrivateKeyPem="$(cat "$RIG/dp.key")" \
Auth__ApiKey="$(cat "$RIG/api.key")" Browser__Enabled=false \
Ee__License__ServiceUrl="${LS_URL:-http://127.0.0.1:18500}" \
Ee__License__ReportIntervalHours="${REPORT_HOURS:-24}" Ee__License__RefreshSeconds=5 \
HOME="$RIG/home" DOTNET_GCHeapHardLimit=0x30000000 \
exec dotnet "${BACKEND_BIN:-$RIG/backend-bin}/AgentHub.Api.dll"
