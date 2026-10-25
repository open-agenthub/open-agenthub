#!/usr/bin/env bash
# One real Claude Code turn (-p, as an unattended session runs) against the mock model, exporting
# OTLP to the hub with the env AgentPodSpecFactory gives a Claude pod (telemetry.enabled).
# usage: run-claude.sh <home> <session-id> <owner> <extra-resource-attrs|-> <subscription|apikey|both> <hub> <model-url>
set -eu
H=$1; SID=$2; OWNER=$3; EXTRA=$4; AUTH=$5; HUB=$6; MODEL=$7
# Extra resource attributes stand in for a pod that tries to claim something in its payload.
ATTRS="agenthub.session_id=$SID,agenthub.owner=$OWNER"; [ "$EXTRA" = - ] || ATTRS="$ATTRS,$EXTRA"
mkdir -p "$H/.claude" "$H/work"
echo '{"hasCompletedOnboarding":true}' > "$H/.claude.json"
KEY=()
if [ "$AUTH" != apikey ]; then
  # A fake login: the mock model accepts any bearer token; the CLI only needs a well-formed file.
  printf '{"claudeAiOauth":{"accessToken":"sk-ant-oat01-rig","refreshToken":"sk-ant-ort01-rig","expiresAt":%s,"scopes":["user:inference","user:profile"],"subscriptionType":"max"}}' \
    "$(( ($(date +%s) + 86400) * 1000 ))" > "$H/.claude/.credentials.json"
fi
# An Auto pod gets both: the login file and ANTHROPIC_API_KEY.
[ "$AUTH" = subscription ] || KEY=(ANTHROPIC_API_KEY=sk-ant-api03-rigrigrig)
cd "$H/work"
env -i PATH=/usr/bin:/bin:/usr/local/bin HOME="$H" ANTHROPIC_BASE_URL="$MODEL" "${KEY[@]}" \
  CLAUDE_CODE_ENABLE_TELEMETRY=1 OTEL_METRICS_EXPORTER=otlp OTEL_TRACES_EXPORTER=none OTEL_LOGS_EXPORTER=none \
  OTEL_EXPORTER_OTLP_PROTOCOL=http/protobuf OTEL_EXPORTER_OTLP_ENDPOINT="$HUB/internal/otel" \
  OTEL_EXPORTER_OTLP_METRICS_ENDPOINT="$HUB/internal/otel/v1/metrics" \
  OTEL_EXPORTER_OTLP_METRICS_TEMPORALITY_PREFERENCE=delta OTEL_METRIC_EXPORT_INTERVAL=30000 \
  OTEL_RESOURCE_ATTRIBUTES="$ATTRS" \
  timeout 90 claude -p "say hi" --model claude-sonnet-4-5 </dev/null
