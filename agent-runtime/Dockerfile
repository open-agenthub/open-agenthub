# ---- builder: compile node-pty natively ----
FROM node:22-bookworm AS builder
WORKDIR /opt/session-agent
COPY session-agent/package.json .
RUN apt-get update && apt-get install -y --no-install-recommends python3 make g++ \
 && npm install --omit=dev \
 && apt-get purge -y python3 make g++ && apt-get autoremove -y

# ---- runtime (non-root) ----
FROM node:22-bookworm-slim
# CLI versions for the GitHub / GitLab command-line tools (bump as needed).
ARG GH_VERSION=2.63.2
ARG GLAB_VERSION=1.48.0
RUN apt-get update && apt-get install -y --no-install-recommends \
      git openssh-client ca-certificates bash curl tar \
 && npm install -g @anthropic-ai/claude-code \
 # GitHub CLI (gh) + GitLab CLI (glab) — so agents can open PRs/MRs, browse issues, etc.
 && mkdir -p /tmp/gh /tmp/glab \
 && curl -fsSL "https://github.com/cli/cli/releases/download/v${GH_VERSION}/gh_${GH_VERSION}_linux_amd64.tar.gz" | tar xz -C /tmp/gh \
 && install -m0755 "$(find /tmp/gh -type f -name gh | head -1)" /usr/local/bin/gh \
 && curl -fsSL "https://gitlab.com/gitlab-org/cli/-/releases/v${GLAB_VERSION}/downloads/glab_${GLAB_VERSION}_linux_amd64.tar.gz" | tar xz -C /tmp/glab \
 && install -m0755 "$(find /tmp/glab -type f -name glab | head -1)" /usr/local/bin/glab \
 && rm -rf /tmp/gh /tmp/glab /var/lib/apt/lists/* \
 # The base image already ships a "node" user with UID 1000 – replace it with "agent"
 && userdel -r node \
 && useradd -m -u 1000 -s /bin/bash agent

COPY --from=builder /opt/session-agent/node_modules /opt/session-agent/node_modules
COPY session-agent/server.js           /opt/session-agent/server.js
COPY session-agent/notify-hook.sh      /opt/session-agent/notify-hook.sh
COPY session-agent/pretooluse-hook.sh  /opt/session-agent/pretooluse-hook.sh
COPY entrypoint.sh                     /usr/local/bin/entrypoint.sh
RUN chmod +x /usr/local/bin/entrypoint.sh /opt/session-agent/notify-hook.sh /opt/session-agent/pretooluse-hook.sh

USER 1000:1000
ENV HOME=/home/agent
WORKDIR /workspace
ENTRYPOINT ["/usr/local/bin/entrypoint.sh"]
