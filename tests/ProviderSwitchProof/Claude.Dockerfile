FROM example.com/provider-switch-claude-base:local
USER root
COPY --chmod=0644 proof.crt /usr/local/share/ca-certificates/provider-proof.crt
RUN update-ca-certificates
ENV NODE_EXTRA_CA_CERTS=/usr/local/share/ca-certificates/provider-proof.crt
USER 1000:1000
ARG MODEL_BASE
ENV ANTHROPIC_BASE_URL=${MODEL_BASE}
