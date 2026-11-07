FROM example.com/provider-switch-codex-base:local
USER root
COPY --chmod=0644 proof.crt /usr/local/share/ca-certificates/provider-proof.crt
RUN update-ca-certificates && mv /usr/local/bin/codex /usr/local/bin/codex-native
ENV CODEX_CA_CERTIFICATE=/usr/local/share/ca-certificates/provider-proof.crt
COPY --chmod=0755 codex-native.sh /usr/local/bin/codex
ARG MODEL_BASE
ENV PROOF_MODEL_BASE=${MODEL_BASE}
USER 1000:1000
