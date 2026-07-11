# ---- builder: compile node-pty natively ----
FROM node:22-bookworm AS builder
WORKDIR /opt/session-agent
COPY session-agent/package.json .
RUN apt-get update && apt-get install -y --no-install-recommends python3 make g++ \
 && npm install --omit=dev \
 && apt-get purge -y python3 make g++ && apt-get autoremove -y

# ---- runtime (non-root) ----
FROM node:22-bookworm-slim
RUN apt-get update && apt-get install -y --no-install-recommends \
      git openssh-client ca-certificates bash curl tar \
 && npm install -g @anthropic-ai/claude-code \
 && rm -rf /var/lib/apt/lists/* \
 # The base image already ships a "node" user with UID 1000 – replace it with "agent"
 && userdel -r node \
 && useradd -m -u 1000 -s /bin/bash agent

COPY --from=builder /opt/session-agent/node_modules /opt/session-agent/node_modules
COPY session-agent/server.js       /opt/session-agent/server.js
COPY session-agent/notify-hook.sh  /opt/session-agent/notify-hook.sh
COPY entrypoint.sh                 /usr/local/bin/entrypoint.sh
RUN chmod +x /usr/local/bin/entrypoint.sh /opt/session-agent/notify-hook.sh

USER 1000:1000
ENV HOME=/home/agent
WORKDIR /workspace
ENTRYPOINT ["/usr/local/bin/entrypoint.sh"]
