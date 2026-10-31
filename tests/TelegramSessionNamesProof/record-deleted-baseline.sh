#!/usr/bin/env bash
set -euo pipefail
# The #365 scenario against the last revision without the delete hook, in its own worktree so
# rebuilding it never touches the working branch. Prints the same steps as
# `dotnet run -- --deleted-sessions`; the assertions fail there, which is the point.
source_root="$(git rev-parse --show-toplevel)"
baseline_dir="${1:?pass an unused absolute worktree path}"
output="${2:?pass an output path outside the repository}"
baseline_branch="proof/365-baseline-$(date +%s)"
git worktree add -b "$baseline_branch" "$baseline_dir" e0f7533
cp "$source_root"/tests/TelegramSessionNamesProof/*.cs "$source_root"/tests/TelegramSessionNamesProof/*.csproj "$baseline_dir/tests/TelegramSessionNamesProof/"
# Only the constructor argument and the prune call differ; the old prune dropped nothing but
# reply-routing rows older than 30 days.
python3 - "$baseline_dir/tests/TelegramSessionNamesProof/DeletedSessionScenario.cs" <<'PY'
import pathlib, sys
p = pathlib.Path(sys.argv[1])
s = p.read_text()
for old, new in [(', chatCleanup: bindings', ''),
                 ('async () => $"{await bindings.PruneAsync()} stale bindings removed"',
                  'async () => { await bindings.PruneMessagesAsync(); return "PruneMessagesAsync (no binding prune)"; }')]:
    assert old in s, old
    s = s.replace(old, new)
p.write_text(s)
PY
dotnet build "$baseline_dir/tests/TelegramSessionNamesProof" --verbosity quiet >&2
dotnet run --no-build --project "$baseline_dir/tests/TelegramSessionNamesProof" -- --deleted-sessions > "$output" || true
