# ---- builder: compile node-pty natively ----
FROM node:26-bookworm AS builder
WORKDIR /opt/session-agent
COPY session-agent/package.json session-agent/package-lock.json ./
RUN apt-get update && apt-get install -y --no-install-recommends python3 make g++ \
 && npm ci --omit=dev \
 && apt-get purge -y python3 make g++ && apt-get autoremove -y

# ---- runtime (non-root) ----
FROM node:26-bookworm-slim
# Pin discovered from npm registry (stable non-beta) at Task 5 authoring time.
# Auth store (OpenClaw 2026.7.1-2):
#   ~/.openclaw/agents/<agentId>/agent/auth-profiles.json  (logical JSON name)
#   ~/.openclaw/agents/<agentId>/agent/openclaw-agent.sqlite (canonical; store_json)
#   Store shape: { version: 1, profiles: {...}, order?: {...} }
# AgentHub Secret key + watched file: ~/.openclaw/auth-profiles.json
# sync-auth-profiles.js imports Secret → agent SQLite and exports SQLite → Secret JSON.
# sync-auth-profiles.js depends on the auth-store layout documented above, so a bump has
# to be verified by hand — update-agent-runtimes.yml proposes it in a PR but does not
# merge it. Hence this pin trails the other runtimes on purpose.
ARG OPENCLAW_VERSION=2026.7.1-2
ARG GH_VERSION=2.102.0
ARG GLAB_VERSION=1.122.0
# Set by BuildKit per target platform (amd64, arm64); gh and glab use the same names.
ARG TARGETARCH
ENV OPENCLAW_VERSION=${OPENCLAW_VERSION}
RUN apt-get update && apt-get install -y --no-install-recommends \
      git openssh-client ca-certificates bash curl tar \
 && npm install -g openclaw@${OPENCLAW_VERSION} \
 && mkdir -p /tmp/gh /tmp/glab \
 && curl -fsSL "https://github.com/cli/cli/releases/download/v${GH_VERSION}/gh_${GH_VERSION}_linux_${TARGETARCH}.tar.gz" | tar xz -C /tmp/gh \
 && install -m0755 "$(find /tmp/gh -type f -name gh | head -1)" /usr/local/bin/gh \
 && curl -fsSL "https://gitlab.com/gitlab-org/cli/-/releases/v${GLAB_VERSION}/downloads/glab_${GLAB_VERSION}_linux_${TARGETARCH}.tar.gz" | tar xz -C /tmp/glab \
 && install -m0755 "$(find /tmp/glab -type f -name glab | head -1)" /usr/local/bin/glab \
 && rm -rf /tmp/gh /tmp/glab /var/lib/apt/lists/* \
 && userdel -r node \
 && useradd -m -u 1000 -s /bin/bash agent \
 && test -x /usr/local/bin/node \
 && test -x /usr/local/bin/openclaw

COPY --from=builder /opt/session-agent/node_modules /opt/session-agent/node_modules
COPY session-agent/package.json /opt/session-agent/package.json
COPY session-agent/server.js /opt/session-agent/server.js
COPY common /opt/session-agent/common
COPY files /opt/session-agent/files
COPY network /opt/session-agent/network
COPY skills /opt/session-agent/skills
COPY openclaw /opt/session-agent/openclaw
COPY openclaw/entrypoint.sh /usr/local/bin/entrypoint.sh
RUN chmod +x /usr/local/bin/entrypoint.sh \
    /opt/session-agent/common/entrypoint-common.sh \
    /opt/session-agent/common/setup-cli-auth.sh \
    /opt/session-agent/openclaw/entrypoint.sh \
    /opt/session-agent/openclaw/login.sh

USER 1000:1000
ENV HOME=/home/agent
WORKDIR /workspace
ENTRYPOINT ["/usr/local/bin/entrypoint.sh"]
