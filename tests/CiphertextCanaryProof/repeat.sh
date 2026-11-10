#!/usr/bin/env bash
set -euo pipefail
: "${AGENTHUB_TEST_POSTGRES_CONNECTION_STRING:?Set a connection string for real Postgres}"
cd "$(dirname "$0")/../.."
project=tests/AgentHub.Api.Tests
filter='FullyQualifiedName~LibraryStoresPostgresTests|FullyQualifiedName~McpSecretProtectorTests'
dotnet test "$project" --filter "$filter"
dotnet run --project tests/CiphertextCanaryProof/CiphertextCanaryProof.csproj
