#!/usr/bin/env bash
set -euo pipefail
# Keep the baseline isolated: rebuilding it must never overwrite the working branch.
source_root="$(git rev-parse --show-toplevel)"
baseline_dir="${1:?pass an unused absolute worktree path}"
output="${2:?pass an output path outside the repository}"
baseline_branch="proof/363-baseline-$(date +%s)"
git worktree add -b "$baseline_branch" "$baseline_dir" 562677b
mkdir -p "$baseline_dir/tests/TelegramSessionNamesProof"
cp "$source_root"/tests/TelegramSessionNamesProof/*.cs "$source_root"/tests/TelegramSessionNamesProof/*.csproj "$baseline_dir/tests/TelegramSessionNamesProof/"
# The #365 scenario needs the delete hook, which this revision predates; it has its own
# baseline runner (record-deleted-baseline.sh).
rm "$baseline_dir/tests/TelegramSessionNamesProof/DeletedSessionScenario.cs"
python3 - "$baseline_dir/tests/TelegramSessionNamesProof/Program.cs" <<'PY'
import pathlib, re, sys
p = pathlib.Path(sys.argv[1])
p.write_text(re.sub(r'if \(args\.Contains\("--deleted-sessions"\)\)\n\{.*?\n\}\n', '', p.read_text(), flags=re.S))
PY
# The baseline notifier predates the session-lookup dependency.
python3 - "$baseline_dir/tests/TelegramSessionNamesProof/TelegramSessionScenario.cs" <<'PY'
import pathlib, sys
p = pathlib.Path(sys.argv[1])
p.write_text(p.read_text().replace('permissions, sessions);', 'permissions);').replace('ChatFormatting.SessionLabel(', 'ChatFormatting.Header('))
PY
dotnet build "$baseline_dir/tests/TelegramSessionNamesProof" --verbosity quiet >&2
dotnet run --no-build --project "$baseline_dir/tests/TelegramSessionNamesProof" -- --record-baseline > "$output"
