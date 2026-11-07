FROM example.com/provider-switch-codex-base:local
USER root
RUN mv /usr/local/bin/codex /usr/local/bin/codex-native
COPY --chmod=0755 codex-native.sh /usr/local/bin/codex
ARG MODEL_BASE
ENV PROOF_MODEL_BASE=${MODEL_BASE}
USER 1000:1000
