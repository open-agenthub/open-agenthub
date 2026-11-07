#!/usr/bin/env bash
# Simulates the next release for the upgrade proof: the same checkout's images under a new
# tag, and the local chart's appVersion moved to it. Prints the new tag.
set -euo pipefail
old="${GITHUB_SHA:0:7}"
new="$old-next"
registry=ghcr.io/open-agenthub/open-agenthub
# The images are already in k3s's containerd from the startup import; the new names are added
# there directly, since k3s reads its import directory only when it starts.
for component in backend frontend artifact-renderer; do
  sudo k3s ctr -n k8s.io images tag --force "$registry/$component:$old" "$registry/$component:$new" >&2
done
sed -i "s/^appVersion:.*/appVersion: \"$new\"/" helm/open-agenthub/Chart.yaml
echo "$new"
