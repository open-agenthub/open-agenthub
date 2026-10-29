#!/usr/bin/env bash
# Runs the "Open the pull request" lookup/create/edit block of update-agent-runtimes.yml with the
# real gh for the PR lookup and a `gh pr create` that is refused as on the runner. Correct: exit 1
# with an ::error:: line, and no `pr edit` of the merged #47.
set -uo pipefail
here="$(cd "$(dirname "$0")" && pwd)"
step="$(sed -n '/# Only an OPEN pull request/,/^          fi$/p' "$here/../../.github/workflows/update-agent-runtimes.yml" | sed 's/^          //')"
[ -n "$step" ] || { echo "step not found"; exit 2; }
bash -c "
set -euo pipefail
branch=chore/agent-runtime-versions; body=x
gh() { if [ \"\$1 \$2\" = 'pr create' ]; then echo 'pull request create failed: GraphQL: GitHub Actions is not permitted to create or approve pull requests (createPullRequest)' >&2; return 1; fi
       if [ \"\$1 \$2\" = 'pr edit' ]; then echo \"EDIT \$3 (must not happen)\"; return 0; fi
       command gh \"\$@\"; }
$step"
echo "exit=$?"
