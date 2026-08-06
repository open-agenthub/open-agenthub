# ---- builder: compile node-pty natively ----
FROM node:22-bookworm AS builder
WORKDIR /opt/session-agent
COPY session-agent/package.json session-agent/package-lock.json ./
RUN apt-get update && apt-get install -y --no-install-recommends python3 make g++ \
 && npm ci --omit=dev \
 && apt-get purge -y python3 make g++ && apt-get autoremove -y

# ---- runtime (non-root) ----
FROM node:22-bookworm-slim
# Cursor Agent CLI version discovered at image build from https://cursor.com/install
# (symlink target under ~/.local/share/cursor-agent/versions/<version>/). Pin when known.
ARG CURSOR_AGENT_VERSION=2026.08.04-aaa8809
ARG GH_VERSION=2.63.2
ARG GLAB_VERSION=1.48.0
ENV CURSOR_AGENT_VERSION=${CURSOR_AGENT_VERSION}
RUN apt-get update && apt-get install -y --no-install-recommends \
      git openssh-client ca-certificates bash curl tar \
 && curl https://cursor.com/install -fsS | bash \
 && mkdir -p /usr/local/share/cursor-agent \
 && cp -a /root/.local/share/cursor-agent/versions /usr/local/share/cursor-agent/ \
 && AGENT_VER_DIR="$(dirname "$(readlink -f /root/.local/bin/agent)")" \
 && AGENT_VER="$(basename "$AGENT_VER_DIR")" \
 && test -x "/usr/local/share/cursor-agent/versions/${AGENT_VER}/cursor-agent" \
 && ln -sfn "/usr/local/share/cursor-agent/versions/${AGENT_VER}/cursor-agent" /usr/local/bin/agent \
 && ln -sfn "/usr/local/share/cursor-agent/versions/${AGENT_VER}/cursor-agent" /usr/local/bin/cursor-agent \
 && echo "$AGENT_VER" > /usr/local/share/cursor-agent/INSTALLED_VERSION \
 && test "$AGENT_VER" = "$CURSOR_AGENT_VERSION" \
 && agent --version 2>&1 | grep -Fq "$CURSOR_AGENT_VERSION" \
 && mkdir -p /tmp/gh /tmp/glab \
 && curl -fsSL "https://github.com/cli/cli/releases/download/v${GH_VERSION}/gh_${GH_VERSION}_linux_amd64.tar.gz" | tar xz -C /tmp/gh \
 && install -m0755 "$(find /tmp/gh -type f -name gh | head -1)" /usr/local/bin/gh \
 && curl -fsSL "https://gitlab.com/gitlab-org/cli/-/releases/v${GLAB_VERSION}/downloads/glab_${GLAB_VERSION}_linux_amd64.tar.gz" | tar xz -C /tmp/glab \
 && install -m0755 "$(find /tmp/glab -type f -name glab | head -1)" /usr/local/bin/glab \
 && rm -rf /tmp/gh /tmp/glab /root/.local /var/lib/apt/lists/* \
 && userdel -r node \
 && useradd -m -u 1000 -s /bin/bash agent \
 && test -x /usr/local/bin/node \
 && test -x /usr/local/bin/agent

COPY --from=builder /opt/session-agent/node_modules /opt/session-agent/node_modules
COPY session-agent/package.json /opt/session-agent/package.json
COPY session-agent/server.js /opt/session-agent/server.js
COPY common /opt/session-agent/common
COPY files /opt/session-agent/files
COPY sessions /opt/session-agent/sessions
COPY cursor /opt/session-agent/cursor
COPY cursor/entrypoint.sh /usr/local/bin/entrypoint.sh
RUN chmod +x /usr/local/bin/entrypoint.sh \
    /opt/session-agent/common/entrypoint-common.sh \
    /opt/session-agent/common/setup-cli-auth.sh \
    /opt/session-agent/cursor/entrypoint.sh \
    /opt/session-agent/cursor/login.sh

USER 1000:1000
ENV HOME=/home/agent
WORKDIR /workspace
ENTRYPOINT ["/usr/local/bin/entrypoint.sh"]
