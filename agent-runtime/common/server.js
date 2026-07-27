'use strict';

const { loadDriver, validateDriver } = require('./driver-contract');

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
  const execFile = supplied.execFile || require('node:child_process').execFile;
  const fs = supplied.fs || require('node:fs');
  const fetchImpl = supplied.fetch || fetch;
  const processLike = supplied.process || process;
  const setIntervalImpl = supplied.setInterval || setInterval;
  const setTimeoutImpl = supplied.setTimeout || setTimeout;
  const now = supplied.now || Date.now;

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
    const excludedAuth = driver.stateDir + '/' + driver.authFilename;
    execFile('/bin/sh', ['-c',
      'tar czf /tmp/state.tgz -C "' + home + '" --exclude="' + excludedAuth + '" "' + archive +
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
      sendUser(text) {
        const event = { type: 'user', message: { role: 'user', content: [{ type: 'text', text }] } };
        try { child.stdin.write(JSON.stringify(event) + '\n'); } catch { return; }
        // The CLI never echoes user input on stdout, so replays need our copy.
        const echo = JSON.stringify({ ...event, agenthub_echo: true }) + '\n';
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
      chat.sendUser(env.AGENTHUB_PROMPT);
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

  function handleAgent(socket) {
    clients.add(socket);
    if (scrollback) safeSend(socket, scrollback);
    socket.on('message', raw => {
      let message;
      try { message = JSON.parse(raw.toString()); } catch { return; }
      if (exited) return;
      if (chatMode) {
        if (message.type === 'chat' && typeof message.text === 'string' && message.text.trim() && chat) {
          chat.sendUser(message.text);
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

  startAgent(true);

  setIntervalImpl(() => {
    if (!exited) persistAll();
  }, 30_000);

  const webSocketServer = new WebSocketServer({ port, handleProtocols: () => 'tty' });
  webSocketServer.on('connection', (socket, request) => {
    const requestPath = (request && request.url ? request.url : '/').split('?')[0];
    if (requestPath === '/shell') handleShell(socket);
    else handleAgent(socket);
  });

  console.log('[agent] WebSocket terminal listening on :' + port + ' (paths: / and /shell)');
  postStatus('Running');

  for (const signal of ['SIGTERM', 'SIGINT']) {
    processLike.on(signal, () => {
      persistAll(() => {
        try { term.kill(); } catch {}
        processLike.exit(0);
      });
    });
  }

  return { env, webSocketServer };
}

function startFromEnvironment(options = {}) {
  const env = options.env || process.env;
  const driver = loadDriver(env.AGENTHUB_DRIVER);
  return createCommonServer({ env, driver, dependencies: options.dependencies });
}

if (require.main === module) startFromEnvironment();

module.exports = { createCommonServer, startFromEnvironment, MAX_BUFFER };
