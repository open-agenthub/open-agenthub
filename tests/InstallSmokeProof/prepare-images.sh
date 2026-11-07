#!/usr/bin/env bash
set -euo pipefail

# Match build-images.yml's type=sha,prefix= tag, including the PR merge checkout.
tag="${GITHUB_SHA:0:7}"
registry=ghcr.io/open-agenthub/open-agenthub
images=()
for component in backend frontend artifact-renderer; do
  image="$registry/$component:$tag"
  images+=("$image")
done

if [ "${INJECT_STARTUP_FAILURE:-false}" = true ]; then
  echo "::warning::Injecting deliberate backend startup failure for workflow_dispatch proof"
  # Exercise the installer's real Helm readiness gate with an image that cannot start.
  printf 'FROM %s/backend:%s\nENTRYPOINT ["/bin/sh", "-c", "echo deliberate-startup-failure >&2; exit 1"]\n' "$registry" "$tag" |
    docker build -t "$registry/backend:$tag" -
fi

# k3s imports this archive on first startup, so the unmodified quickstart still
# creates the cluster. No registry credentials or published PR packages are needed.
sudo mkdir -p /var/lib/rancher/k3s/agent/images
docker save "${images[@]}" | sudo tee /var/lib/rancher/k3s/agent/images/install-smoke.tar >/dev/null
# The installer accepts a chart path but no image override. Give the local chart copy the
# checkout's tag as its appVersion and leave image.tag at its empty default, so the install
# takes the same path as a released chart: chart X selects the images of X (#491).
# Quoted: a short SHA such as 4317158e6 is a float to YAML and became the tag "4.317158e+06".
grep -q '^  tag: ""$' helm/open-agenthub/values.yaml ||
  { echo "::error::image.tag is no longer empty by default; this proof would not test the default" >&2; exit 1; }
sed -i "s/^appVersion:.*/appVersion: \"$tag\"/" helm/open-agenthub/Chart.yaml
echo "Testing checkout $GITHUB_SHA with locally built images via the chart appVersion:"
printf '  %s\n' "${images[@]}"
