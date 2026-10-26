// The stand-in pod's agent: the REAL common/server.js (as the image runs it) with a minimal driver
// whose "agent" is an idle shell. It is what answers the hub's PUT /agenthub/git-credentials and
// rewrites ~/.git-credentials. BIND_HOST pins it to the pod's own loopback address, so two
// stand-in pods can share the agent port the way two real pods do.
const http = require('node:http');
const listen = http.Server.prototype.listen;
http.Server.prototype.listen = function (port, ...rest) {
  return typeof port === 'number' ? listen.call(this, port, process.env.BIND_HOST, ...rest) : listen.call(this, port, ...rest);
};
const { createCommonServer } = require(process.env.RUNTIME + '/common/server.js');
createCommonServer({
  env: { ...process.env, AGENTHUB_MODE: 'interactive', AGENTHUB_WORKDIR: process.env.HOME },
  driver: {
    name: 'Proof', stateDir: '.proof', authFilename: 'auth.json',
    attachmentCapabilities: { nativeImages: false, localImagePaths: true, mcpImages: true },
    prepare() {}, buildCommand: () => ({ cmd: 'bash', args: ['-c', 'sleep infinity'] }),
    isResumeCommand: () => false, isMissingResume: () => false
  },
  dependencies: { pty: require('/opt/session-agent/node_modules/node-pty'), WebSocketServer: require('/opt/session-agent/node_modules/ws').WebSocketServer }
});
