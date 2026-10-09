# ---- builder: compile node-pty natively ----
FROM node:22-bookworm AS builder
WORKDIR /opt/session-agent
COPY session-agent/package.json session-agent/package-lock.json ./
RUN apt-get update && apt-get install -y --no-install-recommends python3 make g++ \
 && npm ci --omit=dev \
 && apt-get purge -y python3 make g++ && apt-get autoremove -y

# ---- runtime (non-root) ----
FROM node:22-bookworm-slim
# Pinned so a rebuild of this commit yields the same CLI. The policy plugin depends on
# tool.execute.before firing for every tool call and the credential watcher on the auth.json
# layout, so update-agent-runtimes.yml reports a newer version instead of applying it — see
# docs/agent-runtime-updates.md for what to check by hand.
ARG OPENCODE_VERSION=1.18.35
ARG GH_VERSION=2.102.0
ARG GLAB_VERSION=1.120.0
# Set by BuildKit per target platform (amd64, arm64); gh and glab use the same names.
ARG TARGETARCH
ENV OPENCODE_VERSION=${OPENCODE_VERSION}
# The npm package's postinstall picks a binary by probing the *build* machine's CPU: on a runner
# with AVX2 it installs the AVX2 build, which then dies with an illegal instruction on a node
# without it. The binary is chosen here instead — the baseline build on amd64, which runs
# everywhere — and the platform packages are dropped afterwards: each is a second ~180 MB copy,
# and the custom-image init container copies the whole global node_modules into every pod.
RUN apt-get update && apt-get install -y --no-install-recommends \
      git openssh-client ca-certificates bash curl tar \
 && npm install -g opencode-ai@${OPENCODE_VERSION} \
 && pkg=/usr/local/lib/node_modules/opencode-ai \
 && case "$TARGETARCH" in \
      amd64) platform=linux-x64-baseline ;; \
      arm64) platform=linux-arm64 ;; \
      *) echo "unsupported TARGETARCH '$TARGETARCH'" >&2; exit 1 ;; \
    esac \
 && cp "$pkg/node_modules/opencode-$platform/bin/opencode" "$pkg/bin/opencode.exe.new" \
 && mv -f "$pkg/bin/opencode.exe.new" "$pkg/bin/opencode.exe" \
 && chmod 0755 "$pkg/bin/opencode.exe" \
 && rm -rf "$pkg/node_modules" \
 && opencode --version | grep -Fxq "$OPENCODE_VERSION" \
 && mkdir -p /tmp/gh /tmp/glab \
 && curl -fsSL "https://github.com/cli/cli/releases/download/v${GH_VERSION}/gh_${GH_VERSION}_linux_${TARGETARCH}.tar.gz" | tar xz -C /tmp/gh \
 && install -m0755 "$(find /tmp/gh -type f -name gh | head -1)" /usr/local/bin/gh \
 && curl -fsSL "https://gitlab.com/gitlab-org/cli/-/releases/v${GLAB_VERSION}/downloads/glab_${GLAB_VERSION}_linux_${TARGETARCH}.tar.gz" | tar xz -C /tmp/glab \
 && install -m0755 "$(find /tmp/glab -type f -name glab | head -1)" /usr/local/bin/glab \
 && rm -rf /tmp/gh /tmp/glab /root/.cache /root/.local /root/.config /var/lib/apt/lists/* \
 && userdel -r node \
 && useradd -m -u 1000 -s /bin/bash agent \
 && test -x /usr/local/bin/node \
 && test -x /usr/local/bin/opencode

COPY --from=builder /opt/session-agent/node_modules /opt/session-agent/node_modules
COPY session-agent/package.json /opt/session-agent/package.json
COPY session-agent/server.js /opt/session-agent/server.js
COPY common /opt/session-agent/common
COPY files /opt/session-agent/files
COPY browser /opt/session-agent/browser
COPY sessions /opt/session-agent/sessions
COPY network /opt/session-agent/network
COPY skills /opt/session-agent/skills
COPY opencode /opt/session-agent/opencode
COPY opencode/entrypoint.sh /usr/local/bin/entrypoint.sh
RUN chmod +x /usr/local/bin/entrypoint.sh \
    /opt/session-agent/common/entrypoint-common.sh \
    /opt/session-agent/common/setup-cli-auth.sh \
    /opt/session-agent/opencode/entrypoint.sh \
    /opt/session-agent/opencode/login.sh

USER 1000:1000
ENV HOME=/home/agent
WORKDIR /workspace
ENTRYPOINT ["/usr/local/bin/entrypoint.sh"]
