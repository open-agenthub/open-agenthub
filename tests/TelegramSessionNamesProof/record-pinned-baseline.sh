#!/usr/bin/env bash
set -euo pipefail
# The #398 scenario against main before the pin, in its own worktree so rebuilding it never
# touches the working branch. Prints the same steps as `dotnet run -- --pinned-session`
# without the assertions: the point is the old wire text and where the plain message went.
source_root="$(git rev-parse --show-toplevel)"
baseline_dir="${1:?pass an unused absolute worktree path}"
output="${2:?pass an output path outside the repository}"
baseline_branch="proof/398-baseline-$(date +%s)"
git worktree add -b "$baseline_branch" "$baseline_dir" 680f9e1
# The scenario uses only store and router APIs that exist before the change; the pin table is
# read through to_regclass, so its absence prints "(no pin table)" instead of failing.
cp "$source_root"/tests/TelegramSessionNamesProof/*.cs "$source_root"/tests/TelegramSessionNamesProof/*.csproj "$baseline_dir/tests/TelegramSessionNamesProof/"
dotnet build "$baseline_dir/tests/TelegramSessionNamesProof" --verbosity quiet >&2
dotnet run --no-build --project "$baseline_dir/tests/TelegramSessionNamesProof" -- --pinned-session --record-baseline > "$output"
