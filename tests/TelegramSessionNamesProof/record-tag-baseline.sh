#!/usr/bin/env bash
set -euo pipefail
source_root="$(git rev-parse --show-toplevel)"
baseline_dir="${1:?pass an unused absolute worktree path}"
output="${2:?pass an output path outside the repository}"
# Keep the pre-normalization backend intact while replaying the same recorded commands.
git worktree add --detach "$baseline_dir" 2b20aa2
cp "$source_root/tests/TelegramSessionNamesProof/TelegramSessionScenario.cs" "$baseline_dir/tests/TelegramSessionNamesProof/TelegramSessionScenario.cs"
dotnet build "$baseline_dir/tests/TelegramSessionNamesProof" --verbosity quiet >&2
dotnet run --no-build --project "$baseline_dir/tests/TelegramSessionNamesProof" -- --record-baseline > "$output"
