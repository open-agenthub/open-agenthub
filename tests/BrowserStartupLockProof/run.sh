#!/usr/bin/env bash
set -euo pipefail
: "${AGENTHUB_TEST_POSTGRES_CONNECTION_STRING:?Set a disposable database connection string}"
dotnet test tests/AgentHub.Api.Tests --filter FullyQualifiedName~KubernetesBrowserServiceTests --logger 'console;verbosity=normal'
