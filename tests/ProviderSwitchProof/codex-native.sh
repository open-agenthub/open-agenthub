#!/bin/sh
# Keep the native provider and default model while directing this proof's API to its fixture.
exec /usr/local/bin/codex-native -c "openai_base_url=\"$PROOF_MODEL_BASE/v1\"" -c "chatgpt_base_url=\"$PROOF_MODEL_BASE/backend-api\"" "$@"
