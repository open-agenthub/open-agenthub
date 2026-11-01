#!/usr/bin/env bash
set -euo pipefail

# Match build-images.yml's type=sha,prefix= tag, including the PR merge checkout.
tag="${GITHUB_SHA:0:7}"
registry=ghcr.io/open-agenthub/open-agenthub
images=()
for component in backend frontend artifact-renderer; do
  context=.
  if [ "$component" = frontend ]; then context=./frontend; fi
  image="$registry/$component:$tag"
  docker buildx build --load --platform linux/amd64 \
    --build-arg "APP_VERSION=$tag" \
    -f "$component/Dockerfile" -t "$image" "$context"
  images+=("$image")
done

if [ "${BROKEN_BACKEND:-false}" = true ]; then
  # Exercise the installer's real Helm readiness gate with an image that cannot start.
  printf 'FROM %s/backend:%s\nENTRYPOINT ["/bin/sh", "-c", "echo deliberate-startup-failure >&2; exit 1"]\n' "$registry" "$tag" |
    docker build -t "$registry/backend:$tag" -
fi

# k3s imports this archive on first startup, so the unmodified quickstart still
# creates the cluster. No registry credentials or published PR packages are needed.
sudo mkdir -p /var/lib/rancher/k3s/agent/images
docker save "${images[@]}" | sudo tee /var/lib/rancher/k3s/agent/images/install-smoke.tar >/dev/null
# The installer accepts a chart path but no image override. Pin only its local
# chart copy; keep the same pin for the subsequent upgrade-in-place.
sed -i "s/^  tag: latest$/  tag: $tag/" helm/open-agenthub/values.yaml
echo "Testing checkout $GITHUB_SHA with locally built images (no :latest fallback):"
printf '  %s\n' "${images[@]}"
