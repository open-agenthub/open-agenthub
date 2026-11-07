#!/usr/bin/env bash
# Deterministic stand-in for the loaded machine behind #518. Copies share-session-dialog.test.js
# with a 300 ms synchronous stall at the start of the picker tests' setup, which is how long a
# busy CI worker took there, so a 250 ms lookup timer still pending from an earlier test comes
# due inside the picker test every time instead of now and then.
# Usage: run.sh <frontend dir> [runs=50] [ref]   ref: take the test file from this git ref
#        (e.g. origin/main for the baseline); default is the working tree.
set -u
FRONTEND=$(cd "${1:?frontend dir}" && pwd); RUNS=${2:-50}; REF=${3:-}
SRC=$FRONTEND/src/components/share-session-dialog.test.js
COPY=$FRONTEND/src/components/flake518-proof.test.js
trap 'rm -f "$COPY"' EXIT
if [ -n "$REF" ]; then git -C "$FRONTEND" show "$REF:frontend/src/components/share-session-dialog.test.js" > "$COPY"
else cp "$SRC" "$COPY"; fi
# The clock is bound at import: on main a failed picker test leaves fake timers installed, and a
# stall on the faked Date.now would never end.
STALL='    { const until = realNow() + 300; while (realNow() < until) { /* stall */ } }'
grep -q '^    vi.useFakeTimers()$' "$COPY" || { echo "anchor not found"; exit 2; }
sed -i "s|^    vi.useFakeTimers()\$|$STALL\n    vi.useFakeTimers()|" "$COPY"
sed -i "1a const realNow = Date.now.bind(Date)" "$COPY"
pass=0; fail=0
cd "$FRONTEND"
for i in $(seq "$RUNS"); do
  if out=$(timeout 120 npx vitest run src/components/flake518-proof.test.js 2>&1); then pass=$((pass+1))
  else fail=$((fail+1)); [ $fail -eq 1 ] && echo "$out" | grep -E "FAIL|AssertionError|Array \[|^ +\"" | head -12; fi
done
echo "stalled picker tests: $pass/$RUNS runs passed, $fail failed"
