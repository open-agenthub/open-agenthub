#!/bin/sh
# Verifies, from inside a session, that the skill library works the way the agent is told
# it works: an upload that names a path, a download that writes to a directory, and the
# curl commands the hub prints in a get_skill result.
#
# The unit tests cover all of this against fakes. What they cannot cover is whether the
# pod can actually reach those urls — an egress policy, a missing callback token or a
# service that only resolves from the control namespace all look like working code and
# fail only here. Run it after a deploy:
#
#   sh $RUNTIME/skills/probe.sh
#
# It creates (or updates) one skill named "skill-library-probe" and leaves it in place;
# every run is a new version of that same skill rather than a new skill.

set -eu

RUNTIME="${RUNTIME:-/opt/session-agent}"
SKILL_NAME=skill-library-probe
: "${AGENTHUB_CALLBACK_URL:?AGENTHUB_CALLBACK_URL is not set — is this a session pod?}"
: "${AGENTHUB_CALLBACK_TOKEN:?AGENTHUB_CALLBACK_TOKEN is not set — is this a session pod?}"

work="$(mktemp -d "${TMPDIR:-/tmp}/skill-probe.XXXXXX")"
trap 'rm -rf "$work"' EXIT HUP INT TERM
failures=0

ok()   { printf '  ok    %s\n' "$1"; }
fail() { printf '  FAIL  %s\n' "$1"; failures=$((failures + 1)); }

# A non-ASCII payload on purpose: the result is plain text precisely so umlauts and emoji
# do not arrive as \uXXXX escapes, and that only shows up with characters like these.
mkdir -p "$work/skill/scripts"
cat > "$work/skill/SKILL.md" <<'MD'
---
name: skill-library-probe
description: Written by skills/probe.sh to check that uploads and downloads work.
---
# Probe

Größe prüfen 🚀 — if you are reading this in the library, the upload path works.
MD
printf '#!/bin/sh\necho "probe"\n' > "$work/skill/scripts/check.sh"
# Padded past the point where quoting it into a tool call would be expensive, which is the
# case this whole path exists for.
i=0
while [ "$i" -lt 600 ]; do
  printf '# filler line %s to make this script worth not reading\n' "$i" >> "$work/skill/scripts/check.sh"
  i=$((i + 1))
done
size=$(wc -c < "$work/skill/scripts/check.sh" | tr -d ' ')

printf 'Skill library probe\n'
printf 'hub: %s\n' "$AGENTHUB_CALLBACK_URL"
printf 'helper script: %s bytes\n\n' "$size"

printf '1. upload_skill with a local path (through the session runtime)\n'
upload=$(printf '%s\n' "{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"tools/call\",\"params\":{\"name\":\"upload_skill\",\"arguments\":{\"name\":\"$SKILL_NAME\",\"description\":\"Written by skills/probe.sh.\",\"path\":\"$work/skill\",\"comment\":\"probe run\"}}}" \
  | node "$RUNTIME/skills/server.mjs" 2>"$work/upload.err" || true)
if printf '%s' "$upload" | grep -q '"isError":true'; then
  fail "upload rejected: $(printf '%s' "$upload" | head -c 400)"
elif printf '%s' "$upload" | grep -q 'none of it passed through your context'; then
  ok 'uploaded from the path, file content never entered the context'
else
  fail "unexpected upload result: $(printf '%s' "$upload" | head -c 400)$(cat "$work/upload.err")"
fi

printf '2. get_skill with out_dir (writes the skill to disk)\n'
out="$work/out"
written=$(printf '%s\n' "{\"jsonrpc\":\"2.0\",\"id\":2,\"method\":\"tools/call\",\"params\":{\"name\":\"get_skill\",\"arguments\":{\"name\":\"$SKILL_NAME\",\"out_dir\":\"$out\"}}}" \
  | node "$RUNTIME/skills/server.mjs" 2>"$work/get.err" || true)
if [ -f "$out/scripts/check.sh" ] && [ -f "$out/SKILL.md" ]; then
  if cmp -s "$out/scripts/check.sh" "$work/skill/scripts/check.sh"; then
    ok "written to $out, helper script identical"
  else
    fail 'the written helper script differs from what was uploaded'
  fi
else
  fail "out_dir did not produce the files: $(printf '%s' "$written" | head -c 400)$(cat "$work/get.err")"
fi
if printf '%s' "$written" | grep -q 'filler line'; then
  fail 'the reply contained the file content — the point of out_dir is that it does not'
else
  ok 'the reply names the paths only'
fi

printf '3. the download urls from a plain get_skill, fetched with curl\n'
detail=$(printf '%s\n' "{\"jsonrpc\":\"2.0\",\"id\":3,\"method\":\"tools/call\",\"params\":{\"name\":\"get_skill\",\"arguments\":{\"name\":\"$SKILL_NAME\"}}}" \
  | node "$RUNTIME/skills/server.mjs" 2>/dev/null || true)
skill_id=$(printf '%s' "$detail" | sed -n 's/.*id \([a-f0-9]\{6,\}\)).*/\1/p' | head -1)
version=$(printf '%s' "$detail" | sed -n 's/.*(v\([0-9]*\),.*/\1/p' | head -1)
if [ -z "$skill_id" ] || [ -z "$version" ]; then
  fail "could not read the skill id and version out of the result: $(printf '%s' "$detail" | head -c 300)"
else
  ok "skill $skill_id, version $version"

  file_url="$AGENTHUB_CALLBACK_URL/skills/$skill_id/files/scripts/check.sh?version=$version"
  if curl -fsS -H "X-Agent-Token: $AGENTHUB_CALLBACK_TOKEN" -o "$work/curl-check.sh" "$file_url" \
     && cmp -s "$work/curl-check.sh" "$work/skill/scripts/check.sh"; then
    ok 'single file reachable with curl and byte-identical'
  else
    fail "single file download failed: $file_url"
  fi

  bundle_url="$AGENTHUB_CALLBACK_URL/skills/$skill_id/files.tar.gz?version=$version"
  mkdir -p "$work/bundle"
  if curl -fsS -H "X-Agent-Token: $AGENTHUB_CALLBACK_TOKEN" "$bundle_url" | tar xzf - -C "$work/bundle" \
     && cmp -s "$work/bundle/scripts/check.sh" "$work/skill/scripts/check.sh"; then
    ok 'tar.gz bundle reachable with curl and byte-identical'
  else
    fail "bundle download failed: $bundle_url"
  fi

  # The token is what authorises these routes; without it they must not serve anything.
  if curl -fsS -o /dev/null "$file_url" 2>/dev/null; then
    fail 'the download url served the file without the session token'
  else
    ok 'no token, no file'
  fi
fi

printf '4. non-ASCII text survives without escapes\n'
if printf '%s' "$detail" | grep -q 'Größe prüfen'; then
  ok 'umlauts arrive as themselves'
elif printf '%s' "$detail" | grep -q 'u00f6\|u00df'; then
  fail 'text arrived with \\uXXXX escapes'
else
  fail 'the probe skill content was not in the result at all'
fi

printf '\n'
if [ "$failures" -eq 0 ]; then
  printf 'All checks passed.\n'
else
  printf '%s check(s) failed.\n' "$failures"
  exit 1
fi
