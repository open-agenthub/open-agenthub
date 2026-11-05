#!/usr/bin/env bash
# Run the actual compiled backend; no authorization or MCP service is replaced.
set -euo pipefail
proof_dir=${1:?temporary output directory}
backend_dll=${2:?compiled AgentHub.Api.dll}
export ASPNETCORE_URLS=http://127.0.0.1:18082
export ConnectionStrings__Postgres=${PROOF_POSTGRES:-Host=127.0.0.1;Port=54329;Username=postgres;Database=proof429}
export Oidc__Authority=https://127.0.0.1:18443/oidc
export Oidc__ClientId=agenthub-proof
export Mcp__PublicUrl=https://127.0.0.1:18443
export SSL_CERT_FILE="$proof_dir/certs/bundle.crt"
export Browser__Enabled=false
export AgentHub__Namespace=proof
export AgentHub__CallbackBaseUrl=http://127.0.0.1:18082
ulimit -c 0
exec "${DOTNET:-dotnet}" "$backend_dll"
