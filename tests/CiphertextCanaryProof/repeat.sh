#!/usr/bin/env bash
set -euo pipefail
: "${AGENTHUB_TEST_POSTGRES_CONNECTION_STRING:?Set a connection string for real Postgres}"
cd "$(dirname "$0")/../.."
project=tests/AgentHub.Api.Tests
filter='FullyQualifiedName~LibraryStoresPostgresTests|FullyQualifiedName~McpSecretProtectorTests'
dotnet test "$project" --filter "$filter"
for ((run=1; run<=200; run++)); do
    dotnet test "$project" --no-build --no-restore --filter 'FullyQualifiedName~LibraryStoresPostgresTests.McpServerStore_PersistsKindAndSecretJson'
    echo "Completed $run/200"
done
