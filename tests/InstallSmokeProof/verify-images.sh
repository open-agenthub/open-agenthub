#!/usr/bin/env bash
# Usage: verify-images.sh <tag>
# Fails unless every project image the release runs or hands to the backend carries <tag>:
# the deployments' pod specs, the pods that actually run, and the runtime images in the
# backend configuration that session and browser pods are started from.
set -euo pipefail
tag="$1"
ns="${RELEASE_NAMESPACE:-agenthub}"
prefix=ghcr.io/open-agenthub/open-agenthub/

for component in backend frontend; do
  expected="$prefix$component:$tag"
  actual=$(kubectl -n "$ns" get deployment "agenthub-$component" -o jsonpath='{.spec.template.spec.containers[0].image}')
  echo "Deployment agenthub-$component: $actual (expected $expected)"
  test "$actual" = "$expected"
done

kubectl -n "$ns" get deployments -o json | jq -e --arg p "$prefix" --arg suffix ":$tag" '
  [.items[].spec.template.spec.containers[].image | select(startswith($p)) | endswith($suffix)] | all'

# A pod still on the old tag after the rollout would mean the upgrade did not move it.
running=$(kubectl -n "$ns" get pods --field-selector=status.phase=Running -o json |
  jq -r --arg p "$prefix" '.items[] | select(.metadata.deletionTimestamp == null)
    | .spec.containers[].image | select(startswith($p))')
echo "Running project images:"; printf '  %s\n' $running
test -n "$running"
for image in $running; do
  case "$image" in *":$tag") ;; *) echo "::error::pod runs $image, not :$tag" >&2; exit 1 ;; esac
done

# Session and browser pods are not started by the smoke test, so prove their images by the
# configuration the backend reads them from.
config=$(kubectl -n "$ns" get configmap -o json | jq -r '.items[].data // {} | to_entries[]
  | select(.key | test("AgentImage$|^Browser__Image$")) | "\(.key)=\(.value)"')
echo "Runtime images in the backend configuration:"; printf '  %s\n' $config
test "$(printf '%s\n' $config | wc -l)" -ge 6
for line in $config; do
  case "$line" in *"=$prefix"*":$tag") ;; *) echo "::error::$line is not on :$tag" >&2; exit 1 ;; esac
done
kubectl -n "$ns" get pods -o jsonpath='{range .items[*]}{.metadata.name}{"\n"}{range .status.containerStatuses[*]}{.image}{" "}{.imageID}{"\n"}{end}{end}'
echo "All project images are on :$tag"
