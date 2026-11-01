#!/usr/bin/env bash
set -euo pipefail
cd "$(dirname "$0")/../.."
: "${AGENTHUB_TEST_POSTGRES_CONNECTION_STRING:?Set a connection string for a disposable Postgres database}"
ulimit -c 0
dotnet test tests/AgentHub.Api.Tests --filter FullyQualifiedName~A_healthy_backend_exits_cleanly_on_sigterm
