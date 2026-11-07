FROM node:26-alpine
RUN apk add --no-cache git
COPY model.mjs /opt/proof/model.mjs
COPY --chown=1000:1000 --chmod=0400 server.key server.crt /opt/proof/
ENV PROOF_TLS_CERT=/opt/proof/server.crt PROOF_TLS_KEY=/opt/proof/server.key
USER 1000:1000
ENTRYPOINT ["node", "/opt/proof/model.mjs"]
