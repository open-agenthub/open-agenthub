#!/usr/bin/env bash
# Points OpenClaw's default model at the provider this session actually authenticated with.
#
# OpenClaw ships openai/gpt-5.5 as its default and keeps it whatever credentials exist, so both
# auth modes came up on OpenAI: an ApiKey session mounting an Anthropic key, and a subscription
# session whose login wrote an Anthropic token profile. Both then failed on the first turn with
# "auth or provider access failed for openai" and offered an /auth flow that cannot fix it,
# because the credential was never the problem.
#
# Sourced from the entrypoint (ApiKey, provider known from the mounted key) and from login.sh
# (subscription, provider known only once a profile exists). Prints what it did and never fails
# the caller: a session that comes up on the wrong model is recoverable from inside the TUI with
# `/model`, one that refuses to start is not.
set -uo pipefail

apply_default_model() {
  local provider="${1:-}"
  local helper="${AGENTHUB_SELECT_MODEL:-$(dirname "${BASH_SOURCE[0]}")/select-model.js}"

  if [ -z "$provider" ]; then
    # Subscription mode: whichever provider the login wrote a profile for.
    provider="$(openclaw models auth list 2>/dev/null \
      | node -e '
const { providerFromProfiles } = require(process.argv[1]);
let input = "";
process.stdin.on("data", chunk => { input += chunk; });
process.stdin.on("end", () => process.stdout.write(providerFromProfiles(input) || ""));
' "$helper")"
  fi

  if [ -z "$provider" ]; then
    echo "[openclaw] no authenticated provider found; leaving the default model alone." >&2
    return 0
  fi

  # --all because without it the catalogue lists only already-configured models, which on a fresh
  # state directory is just the OpenAI default this exists to move away from.
  local model
  if ! model="$(openclaw models list --all --plain --provider "$provider" 2>/dev/null \
      | node "$helper" "$provider")"; then
    echo "[openclaw] OpenClaw offers no model for provider '$provider' in this build." >&2
    return 1
  fi

  if openclaw models set "$model" >/dev/null 2>&1; then
    echo "[openclaw] default model: $model (provider $provider)"
  else
    echo "[openclaw] could not set the default model to $model." >&2
  fi
  return 0
}
