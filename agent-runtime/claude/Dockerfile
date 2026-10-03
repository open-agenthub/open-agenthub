# ---- builder: compile node-pty natively ----
FROM node:22-bookworm AS builder
WORKDIR /opt/session-agent
COPY session-agent/package.json session-agent/package-lock.json ./
RUN apt-get update && apt-get install -y --no-install-recommends python3 make g++ \
 && npm ci --omit=dev \
 && apt-get purge -y python3 make g++ && apt-get autoremove -y

# ---- runtime (non-root) ----
FROM node:22-bookworm-slim
# Pinned so a rebuild of this commit yields the same CLI. update-agent-runtimes.yml
# opens a PR when a newer version ships.
ARG CLAUDE_CODE_VERSION=2.1.288
ARG GH_VERSION=2.102.0
ARG GLAB_VERSION=1.120.0
# Set by BuildKit per target platform (amd64, arm64); gh and glab use the same names.
ARG TARGETARCH
ENV CLAUDE_CODE_VERSION=${CLAUDE_CODE_VERSION}
RUN apt-get update && apt-get install -y --no-install-recommends \
      git openssh-client ca-certificates bash curl tar \
 && npm install -g @anthropic-ai/claude-code@${CLAUDE_CODE_VERSION} \
 && claude --version 2>&1 | grep -Fq "$CLAUDE_CODE_VERSION" \
 && mkdir -p /tmp/gh /tmp/glab \
 && curl -fsSL "https://github.com/cli/cli/releases/download/v${GH_VERSION}/gh_${GH_VERSION}_linux_${TARGETARCH}.tar.gz" | tar xz -C /tmp/gh \
 && install -m0755 "$(find /tmp/gh -type f -name gh | head -1)" /usr/local/bin/gh \
 && curl -fsSL "https://gitlab.com/gitlab-org/cli/-/releases/v${GLAB_VERSION}/downloads/glab_${GLAB_VERSION}_linux_${TARGETARCH}.tar.gz" | tar xz -C /tmp/glab \
 && install -m0755 "$(find /tmp/glab -type f -name glab | head -1)" /usr/local/bin/glab \
 && rm -rf /tmp/gh /tmp/glab /var/lib/apt/lists/* \
 && userdel -r node \
 && useradd -m -u 1000 -s /bin/bash agent \
 && test -x /usr/local/bin/node \
 && test -x /usr/local/bin/claude

COPY --from=builder /opt/session-agent/node_modules /opt/session-agent/node_modules
COPY session-agent/package.json /opt/session-agent/package.json
COPY session-agent/server.js /opt/session-agent/server.js
COPY common /opt/session-agent/common
COPY files /opt/session-agent/files
COPY browser /opt/session-agent/browser
COPY sessions /opt/session-agent/sessions
COPY network /opt/session-agent/network
COPY skills /opt/session-agent/skills
COPY claude /opt/session-agent/claude
COPY claude/entrypoint.sh /usr/local/bin/entrypoint.sh
RUN chmod +x /usr/local/bin/entrypoint.sh \
    /opt/session-agent/common/entrypoint-common.sh \
    /opt/session-agent/common/setup-cli-auth.sh \
    /opt/session-agent/claude/entrypoint.sh \
    /opt/session-agent/claude/hooks/notify-hook.sh \
    /opt/session-agent/claude/hooks/pretooluse-hook.sh \
    /opt/session-agent/claude/hooks/mcp-policy-hook.sh

USER 1000:1000
ENV HOME=/home/agent
WORKDIR /workspace
ENTRYPOINT ["/usr/local/bin/entrypoint.sh"]
