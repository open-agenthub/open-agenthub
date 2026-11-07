FROM node:26-alpine
RUN apk add --no-cache git
COPY model.mjs /opt/proof/model.mjs
USER 1000:1000
ENTRYPOINT ["node", "/opt/proof/model.mjs"]
