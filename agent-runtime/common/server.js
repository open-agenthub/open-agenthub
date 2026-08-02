'use strict';
const crypto = require('node:crypto');

const { loadDriver, validateDriver } = require('./driver-contract');
const { LocalFileStore, LocalFileError } = require('../files/local-store');
const { AttachmentMaterializer } = require('../files/materialize');

const MAX_BUFFER = 1_000_000;
// Protocol chatter that the chat UI only needs live, never on replay.
const TRANSIENT_CHAT_EVENTS = new Set(['stream_event', 'control_response', 'control_request']);

function createCommonServer(options = {}) {
  const env = options.env || process.env;
  const driver = validateDriver(options.driver);
  const supplied = options.dependencies || {};
  const pty = supplied.pty || require('node-pty');
  const spawnProcess = supplied.spawn || require('node:child_process').spawn;
  const WebSocketServer = supplied.WebSocketServer || require('ws').WebSocketServer;
  const http = supplied.http || require('node:http');
  const execFile = supplied.execFile || require('node:child_process').execFile;
  const fs = supplied.fs || require('node:fs');
  const fetchImpl = supplied.fetch || fetch;
  const processLike = supplied.process || process;
  const setIntervalImpl = supplied.setInterval || setInterval;
  const setTimeoutImpl = supplied.setTimeout || setTimeout;
  const now = supplied.now || Date.now;
  const fileStore = supplied.fileStore || new LocalFileStore({
    root: env.AGENTHUB_FILE_ROOT || '/workspace/.agenthub/files' });
  const attachmentMaterializer = supplied.attachmentMaterializer ||
    new AttachmentMaterializer({ env, fetch: fetchImpl, store: fileStore });

  const preparation = driver.prepare(env);
  const childEnv = preparation && preparation.childEnv;
  if (childEnv !== undefined && (!childEnv || typeof childEnv !== 'object' || Array.isArray(childEnv))) {
    throw new Error('Agent driver prepare childEnv must be an object');
  }
  // This scopes values to the provider PTY, not to the provider executable alone:
  // commands and tools launched by the provider inherit this environment. Code
  // running as the agent user is therefore inside the credential trust boundary.
  const agentEnv = childEnv === undefined ? env : { ...env, ...childEnv };

  const port = parseInt(env.AGENTHUB_PORT || '7681', 10);
  const mode = (env.AGENTHUB_MODE || 'interactive').toLowerCase();
  const hasRepo = env.AGENTHUB_HAS_REPO === '1';
  const callback = env.AGENTHUB_CALLBACK_URL || '';
  const token = env.AGENTHUB_CALLBACK_TOKEN || '';
  const statePut = env.AGENTHUB_STATE_PUT_URL || '';
  const scrollPut = env.AGENTHUB_SCROLLBACK_PUT_URL || '';
  const home = env.HOME || '/home/agent';
  const workdir = env.AGENTHUB_WORKDIR || (hasRepo ? '/workspace/repo' : '/workspace');
  const cwd = fs.existsSync(workdir) ? workdir : '/workspace';
  const curlOption = env.AGENTHUB_S3_INSECURE === '1' ? '-k ' : '';
  const configuredFileMax = Number.parseInt(env.AGENTHUB_FILE_MAX_BYTES || '', 10);
  const fileMaxBytes = Number.isSafeInteger(configuredFileMax) && configuredFileMax > 0
    ? configuredFileMax : 50 * 1024 * 1024;

  let scrollback = '';
  const clients = new Set();
  let exited = false;
  let term;
  let chat;
  let retriedFresh = false;
  let attemptedResume = false;
  let attemptOutput = '';
  let launchedAt = 0;
  let chatMode = false;
  let controlCounter = 0;
  let chatDelivery = Promise.resolve();
  let pendingChatDeliveries = 0;
  let promptSent = false;

  function remember(chunk) {
    scrollback += chunk;
    if (scrollback.length > MAX_BUFFER) scrollback = scrollback.slice(-MAX_BUFFER);
  }

  function safeSend(socket, data) {
    if (socket.readyState === socket.OPEN) {
      try { socket.send(data); } catch {}
    }
  }

  function broadcast(data) {
    for (const socket of clients) safeSend(socket, data);
  }

  function persistState(done) {
    if (!statePut) return done && done();
    const archive = driver.stateDir;
    const excludes = [driver.stateDir + '/' + driver.authFilename];
    if (Array.isArray(driver.stateExcludes)) {
      for (const entry of driver.stateExcludes) excludes.push(entry);
    }
    const excludeArgs = excludes.map(entry => '--exclude="' + entry + '"').join(' ');
    execFile('/bin/sh', ['-c',
      'tar czf /tmp/state.tgz -C "' + home + '" ' + excludeArgs + ' "' + archive +
      '" 2>/dev/null && curl -fsS ' + curlOption + '-T /tmp/state.tgz "' + statePut + '"'
    ], () => done && done());
  }

  function persistScrollback(done) {
    if (!scrollPut) return done && done();
    try { fs.writeFileSync('/tmp/scrollback.log', scrollback); } catch {}
    execFile('/bin/sh', ['-c',
      'curl -fsS ' + curlOption + '-T /tmp/scrollback.log "' + scrollPut + '"'
    ], () => done && done());
  }

  function backupScrollback(done) {
    if (!callback) return done && done();
    fetchImpl(callback + '/scrollback', {
      method: 'PUT',
      headers: { 'Content-Type': 'text/plain', 'X-Agent-Token': token },
      body: scrollback
    }).catch(() => {}).finally(() => done && done());
  }

  function persistAll(done) {
    backupScrollback(() => persistScrollback(() => persistState(done)));
  }

  function postStatus(status, done) {
    if (!callback) return done && done();
    fetchImpl(callback + '/status', {
      method: 'POST',
      headers: { 'Content-Type': 'application/json', 'X-Agent-Token': token },
      body: JSON.stringify({ status })
    }).catch(() => {}).finally(() => done && done());
  }

  function agenthubEvent(subtype, fields) {
    return JSON.stringify({ type: 'agenthub', subtype, ...fields }) + '\n';
  }

  function retryMessage() {
    if (chatMode) return agenthubEvent('info', { text: 'No saved conversation to resume — starting fresh.' });
    return '\r\n[agent] No saved conversation to resume — starting fresh.\r\n';
  }

  function endMessage(exitCode, signal) {
    if (chatMode) return agenthubEvent('exit', { code: exitCode, signal: signal || null });
    return '\r\n[agent] Session ended (code ' + exitCode +
      (signal ? ', signal ' + signal : '') + ').\r\n';
  }

  function handleAgentExit(exitCode, signal) {
    const elapsedMs = now() - launchedAt;
    if (attemptedResume && !retriedFresh &&
        driver.isMissingResume(attemptOutput, exitCode, elapsedMs)) {
      retriedFresh = true;
      remember(retryMessage());
      startAgent(false);
      return;
    }

    exited = true;
    const message = endMessage(exitCode, signal);
    remember(message);
    for (const socket of clients) {
      safeSend(socket, message);
      try { socket.close(1000); } catch {}
    }
    persistAll(() => {
      postStatus(exitCode === 0 ? 'Succeeded' : 'Failed', () =>
        setTimeoutImpl(() => processLike.exit(exitCode || 0), mode === 'interactive' ? 1500 : 200));
    });
  }

  function chatEventType(line) {
    try {
      const event = JSON.parse(line);
      return event && typeof event.type === 'string' ? event.type : undefined;
    } catch {
      return undefined;
    }
  }

  function deliverChatLine(line) {
    const payload = line + '\n';
    if (!TRANSIENT_CHAT_EVENTS.has(chatEventType(line))) remember(payload);
    broadcast(payload);
  }

  function startPiped(command) {
    const child = spawnProcess(command.cmd, command.args, {
      cwd, env: agentEnv, stdio: ['pipe', 'pipe', 'pipe']
    });

    let pendingLine = '';
    child.stdout.on('data', chunk => {
      const text = chunk.toString();
      attemptOutput += text;
      pendingLine += text;
      let newline;
      while ((newline = pendingLine.indexOf('\n')) !== -1) {
        const line = pendingLine.slice(0, newline);
        pendingLine = pendingLine.slice(newline + 1);
        if (line.trim()) deliverChatLine(line);
      }
    });

    child.stderr.on('data', chunk => {
      const text = chunk.toString();
      attemptOutput += text;
      const payload = agenthubEvent('stderr', { text });
      remember(payload);
      broadcast(payload);
    });

    child.on('exit', (code, signal) =>
      handleAgentExit(code == null ? 1 : code, signal || undefined));

    chat = {
      async sendUser(text, delivery = {}) {
        const agentText = delivery.agentText ?? text;
        const event = {
          type: 'user',
          message: { role: 'user', content: [{ type: 'text', text: agentText }] }
        };
        await new Promise((resolve, reject) => {
          try {
            child.stdin.write(JSON.stringify(event) + '\n', error => error ? reject(error) : resolve());
          } catch (error) {
            reject(error);
          }
        });
        // The CLI never echoes user input on stdout, so replays need our copy.
        const echoEvent = {
          type: 'user',
          message: { role: 'user', content: [{ type: 'text', text }] },
          agenthub_echo: true
        };
        if (delivery.attachments?.length) echoEvent.attachments = delivery.attachments;
        const echo = JSON.stringify(echoEvent) + '\n';
        remember(echo);
        broadcast(echo);
      },
      interrupt() {
        controlCounter += 1;
        const request = {
          type: 'control_request',
          request_id: 'agenthub-' + controlCounter,
          request: { subtype: 'interrupt' }
        };
        try { child.stdin.write(JSON.stringify(request) + '\n'); } catch {}
      },
      kill() { try { child.kill(); } catch {} }
    };
    term = { write() {}, resize() {}, kill: chat.kill };

    if (!attemptedResume && !promptSent && env.AGENTHUB_PROMPT) {
      promptSent = true;
      void chat.sendUser(env.AGENTHUB_PROMPT).catch(() => {});
    }
  }

  function startAgent(allowResume) {
    const command = driver.buildCommand(env, allowResume);
    attemptedResume = driver.isResumeCommand(command);
    attemptOutput = '';
    launchedAt = now();
    chatMode = command.pipe === true;
    console.log('[agent] driver=' + driver.name + ' mode=' + mode + ' resume=' + attemptedResume +
      (chatMode ? ' ui=chat' : '') +
      ' cwd=' + cwd + ' cmd=' + command.cmd + ' ' + command.args.join(' '));

    if (chatMode) {
      startPiped(command);
      return;
    }

    term = pty.spawn(command.cmd, command.args, {
      name: 'xterm-256color', cols: 120, rows: 32, cwd, env: agentEnv
    });

    term.onData(data => {
      attemptOutput += data;
      remember(data);
      broadcast(data);
    });

    term.onExit(({ exitCode, signal }) => handleAgentExit(exitCode, signal));
  }

  function attachmentDelivery() {
    const capabilities = driver.attachmentCapabilities;
    if (capabilities.localImagePaths) return 'localImagePaths';
    if (capabilities.mcpImages) return 'mcpImages';
    // The current stream-json transport has no native image argument. A future
    // provider protocol may select nativeImages before reaching this fallback.
    throw new Error('attachment_visual_delivery_unavailable');
  }

  function attachmentPrompt(text, attachments) {
    const lines = attachments.map(file => {
      const source = file.visualDelivery === 'localImagePaths'
        ? 'local path "' + file.localPath + '"'
        : 'file id "' + file.id + '" through agenthub_files.read_file';
      return '- ' + file.name + ' (' + file.mimeType + ', ' + file.size + ' bytes): ' + source;
    });
    const instruction = [
      '[AgentHub attachments]',
      ...lines,
      '',
      'Inspect each attached image visually before answering. Use the local visual Read path or agenthub_files.read_file as indicated. Do not infer image contents from the filename.',
      text ? '' : null,
      text || null
    ].filter(value => value !== null);
    return instruction.join('\n');
  }

  async function deliverAttachmentTurn(message) {
    const records = await attachmentMaterializer.materialize(message.attachments);
    const delivery = attachmentDelivery();
    const attachments = records.map(file => Object.freeze({
      ...file,
      visualDelivery: delivery
    }));
    const safeAttachments = attachments.map(file => ({
      id: file.id,
      name: file.name,
      mimeType: file.mimeType,
      size: file.size,
      visualDelivery: file.visualDelivery
    }));
    await chat.sendUser(message.text, {
      agentText: attachmentPrompt(message.text, attachments),
      attachments: safeAttachments
    });
  }

  function deliveryEvent(subtype, message) {
    if (!message.clientTurnId) return;
    return agenthubEvent(subtype, message.clientTurnId
      ? { clientTurnId: message.clientTurnId }
      : {});
  }
  function queueChatMessage(socket, message) {
    const attachments = message.attachments === undefined ? [] : message.attachments;
    if (!Array.isArray(attachments)) {
      safeSend(socket, agenthubEvent('error', { code: 'attachment_delivery_failed', clientTurnId: message.clientTurnId }));
      return;
    }
    pendingChatDeliveries += 1;
    chatDelivery = chatDelivery
      .then(() => attachments.length ? deliverAttachmentTurn({ ...message, attachments }) : chat.sendUser(message.text))
      .then(() => { if (message.clientTurnId) safeSend(socket, deliveryEvent('chat_delivered', message)); })
      .catch(() => safeSend(socket, agenthubEvent('error', { code: 'attachment_delivery_failed', clientTurnId: message.clientTurnId })))
      .finally(() => { pendingChatDeliveries -= 1; });
  }
  function handleAgent(socket) {
    clients.add(socket);
    if (scrollback) safeSend(socket, scrollback);
    socket.on('message', raw => {
      let message;
      try { message = JSON.parse(raw.toString()); } catch { return; }
      if (exited) return;
      if (chatMode) {
        if (message.type === 'chat' && typeof message.text === 'string' && chat) {
          if (message.clientTurnId !== undefined &&
              (typeof message.clientTurnId !== 'string' || !/^[A-Za-z0-9-]{1,64}$/.test(message.clientTurnId))) return;
          const hasAttachments = message.attachments !== undefined;
          if (message.text.trim() || hasAttachments) {
            queueChatMessage(socket, message);
          }
        } else if (message.type === 'interrupt' && chat) {
          chat.interrupt();
        }
        return;
      }
      if (message.type === 'input' && typeof message.data === 'string') {
        term.write(message.data);
      } else if (message.type === 'resize' && message.cols > 0 && message.rows > 0) {
        try { term.resize(message.cols, message.rows); } catch {}
      }
    });
    socket.on('close', () => clients.delete(socket));
    socket.on('error', () => clients.delete(socket));
  }

  function handleShell(socket) {
    let shell;
    try {
      shell = pty.spawn('bash', ['-l'], {
        name: 'xterm-256color', cols: 120, rows: 32, cwd, env
      });
    } catch (error) {
      safeSend(socket, '\r\n[agent] Failed to start shell: ' + (error && error.message) + '\r\n');
      try { socket.close(1011); } catch {}
      return;
    }

    shell.onData(data => safeSend(socket, data));
    shell.onExit(() => { try { socket.close(1000); } catch {} });
    socket.on('message', raw => {
      let message;
      try { message = JSON.parse(raw.toString()); } catch { return; }
      if (message.type === 'input' && typeof message.data === 'string') {
        shell.write(message.data);
      } else if (message.type === 'resize' && message.cols > 0 && message.rows > 0) {
        try { shell.resize(message.cols, message.rows); } catch {}
      }
    });
    const cleanup = () => { try { shell.kill(); } catch {} };
    socket.on('close', cleanup);
    socket.on('error', cleanup);
  }

  function tokenMatches(requestToken) {
    if (!token || typeof requestToken !== 'string') return false;
    const expected = Buffer.from(token);
    const received = Buffer.from(requestToken);
    const length = Math.max(expected.length, received.length, 1);
    const left = Buffer.alloc(length);
    const right = Buffer.alloc(length);
    expected.copy(left);
    received.copy(right);
    return expected.length === received.length && crypto.timingSafeEqual(left, right);
  }

  function sendJson(response, status, value) {
    const body = JSON.stringify(value);
    response.statusCode = status;
    response.setHeader('Content-Type', 'application/json; charset=utf-8');
    response.setHeader('Content-Length', Buffer.byteLength(body));
    response.end(body);
  }

  function fileError(response, status, code) {
    sendJson(response, status, { error: code });
  }

  async function handleFileRequest(request, response) {
    const requestPath = (request.url || '').split('?')[0];
    const match = /^\/agenthub\/files\/([a-f0-9]{32})$/.exec(requestPath);
    if (!match) {
      fileError(response, 404, 'not_found');
      return;
    }
    if (!tokenMatches(request.headers['x-agent-token'])) {
      fileError(response, 401, 'unauthorized');
      return;
    }

    const id = match[1];
    try {
      if (request.method === 'PUT') {
        const name = request.headers['x-agent-file-name'];
        if (typeof name !== 'string' || !name) {
          fileError(response, 400, 'invalid_file_name');
          return;
        }
        const declaredLength = Number.parseInt(request.headers['content-length'] || '', 10);
        if (Number.isFinite(declaredLength) && declaredLength > fileMaxBytes) {
          fileError(response, 413, 'file_too_large');
          request.resume();
          return;
        }
        const stored = await fileStore.put(id, name, request, fileMaxBytes);
        sendJson(response, 201, { id: stored.id, name: stored.name, size: stored.size });
        return;
      }

      if (request.method === 'HEAD') {
        const metadata = await fileStore.head(id);
        if (!metadata) {
          response.statusCode = 404;
          response.end();
          return;
        }
        response.statusCode = 200;
        response.setHeader('Content-Length', metadata.size);
        response.setHeader('X-Agent-File-Name', metadata.name);
        response.end();
        return;
      }

      if (request.method === 'GET') {
        const opened = await fileStore.open(id);
        if (!opened) {
          fileError(response, 404, 'file_not_found');
          return;
        }
        response.statusCode = 200;
        response.setHeader('Content-Type', 'application/octet-stream');
        response.setHeader('Content-Length', opened.size);
        response.setHeader('X-Agent-File-Name', opened.name);
        opened.stream.on('error', () => response.destroy());
        opened.stream.pipe(response);
        return;
      }

      if (request.method === 'DELETE') {
        await fileStore.remove(id);
        response.statusCode = 204;
        response.end();
        return;
      }

      response.setHeader('Allow', 'PUT, GET, HEAD, DELETE');
      fileError(response, 405, 'method_not_allowed');
    } catch (error) {
      const code = error instanceof LocalFileError ? error.code : 'file_io_failed';
      const status = code === 'file_too_large' ? 413
        : code === 'invalid_file_name' || code === 'invalid_file_id' ? 400
          : code === 'managed_root_escape' ? 409 : 500;
      fileError(response, status, code);
    }
  }

  startAgent(true);

  setIntervalImpl(() => {
    if (!exited) persistAll();
  }, 30_000);

  const httpServer = http.createServer((request, response) => {
    Promise.resolve(handleFileRequest(request, response)).catch(() => {
      if (!response.headersSent) fileError(response, 500, 'file_io_failed');
      else response.destroy();
    });
  });
  const webSocketServer = new WebSocketServer({ server: httpServer, handleProtocols: () => 'tty' });
  webSocketServer.on('connection', (socket, request) => {
    const requestPath = (request && request.url ? request.url : '/').split('?')[0];
    if (requestPath === '/shell') handleShell(socket);
    else handleAgent(socket);
  });

  httpServer.listen(port);
  console.log('[agent] Agent server listening on :' + port + ' (paths: /, /shell, /agenthub/files/:id)');
  postStatus('Running');

  for (const signal of ['SIGTERM', 'SIGINT']) {
    processLike.on(signal, () => {
      persistAll(() => {
        try { term.kill(); } catch {}
        processLike.exit(0);
      });
    });
  }

  return { env, httpServer, webSocketServer };
}

function startFromEnvironment(options = {}) {
  const env = options.env || process.env;
  const driver = loadDriver(env.AGENTHUB_DRIVER);
  return createCommonServer({ env, driver, dependencies: options.dependencies });
}

if (require.main === module) startFromEnvironment();

module.exports = { createCommonServer, startFromEnvironment, MAX_BUFFER };
