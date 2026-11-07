# Session telemetry credential proof

Uses the real pinned Claude CLI, the real OTLP controller and Postgres stores through
`tests/UsageProof/host`. No provider login or live model request is needed.

1. Start a local Postgres database and set `ConnectionStrings__Postgres` to its connection string.
2. Build and run `dotnet run --project tests/UsageProof/host` (default port 8080).
3. Start `node tests/UsageProof/mock-model.mjs 18780 <output-directory>`.
4. Run `python3 tests/TelemetryAuthProof/prove.py --cli <pinned-claude-path> --out <output-directory>`.

Use a fresh database for each run. The script seeds two synthetic sessions, captures the real
CLI export through a local forwarding server, and verifies the credential is present. It replays
the actual protobuf with missing/invalid credentials and with different/mixed session IDs, checks
that rejected reports leave both stores unchanged, then verifies an authenticated replay counts.
Output stays outside Git; only the script is versioned.

The configured OTLP runtime is Claude. Codex, Cursor, OpenClaw and OpenCode are not configured
with OTLP exporters by the pod factory; their common usage reporter sends `X-Agent-Token` to
`/internal/sessions/{id}/usage`. The five-runtime pod factory regression checks the callback
credential for each runtime and the exporter header only where an exporter is configured.
