FROM node:26-alpine
RUN apk add --no-cache git
USER 1000:1000
WORKDIR /workspace
ENTRYPOINT ["node", "-e", "require('node:http').createServer((request,response)=>{response.statusCode=404;response.end();}).listen(7681,'0.0.0.0')"]
