FROM example.com/provider-switch-claude-base:local
ARG MODEL_BASE
ENV ANTHROPIC_BASE_URL=${MODEL_BASE}
