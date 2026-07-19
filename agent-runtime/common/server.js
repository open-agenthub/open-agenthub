'use strict';

const { loadDriver, validateDriver } = require('./driver-contract');

const MAX_BUFFER = 1_000_000;

function createCommonServer(options = {}) {
  const env = options.env || process.env;
  const driver = validateDriver(options.driver);
  const supplied = options.dependencies || {};
  const pty = supplied.pty || require('node-pty');
  const WebSocketServer = supplied.WebSocketServer || require('ws').WebSocketServer;
  const execFile = supplied.execFile || require('node:child_process').execFile;
  const fs = supplied.fs || require('node:fs');
  const fetchImpl = supplied.fetch || fetch;
  const processLike = supplied.process || process;
  const setIntervalImpl = supplied.setInterval || setInterval;
  const setTimeoutImpl = supplied.setTimeout || setTimeout;
  const now = supplied.now || Date.now;

  driver.prepare(env);

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
  let retriedFresh = false;
  let attemptedResume = false;
  let attemptOutput = '';
  let launchedAt = 0;

  function remember(chunk) {
    scrollback += chunk;
    if (scrollback.length > MAX_BUFFER) scrollback = scrollback.slice(-MAX_BUFFER);
  }

  function safeSend(socket, data) {
    if (socket.readyState === socket.OPEN) {
      try { socket.send(data); } catch {}
    }
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

  function startAgent(allowResume) {
    const command = driver.buildCommand(env, allowResume);
    attemptedResume = driver.isResumeCommand(command);
    attemptOutput = '';
    launchedAt = now();
    console.log('[agent] driver=' + driver.name + ' mode=' + mode + ' resume=' + attemptedResume +
      ' cwd=' + cwd + ' cmd=' + command.cmd + ' ' + command.args.join(' '));
    term = pty.spawn(command.cmd, command.args, {
      name: 'xterm-256color', cols: 120, rows: 32, cwd, env
    });

    term.onData(data => {
      attemptOutput += data;
      remember(data);
      for (const socket of clients) safeSend(socket, data);
    });

    term.onExit(({ exitCode, signal }) => {
      const elapsedMs = now() - launchedAt;
      if (attemptedResume && !retriedFresh &&
          driver.isMissingResume(attemptOutput, exitCode, elapsedMs)) {
        retriedFresh = true;
        remember('\r\n[agent] No saved conversation to resume — starting fresh.\r\n');
        startAgent(false);
        return;
      }

      exited = true;
      const message = '\r\n[agent] Session ended (code ' + exitCode +
        (signal ? ', signal ' + signal : '') + ').\r\n';
      remember(message);
      for (const socket of clients) {
        safeSend(socket, message);
        try { socket.close(1000); } catch {}
      }
      persistAll(() => {
        postStatus(exitCode === 0 ? 'Succeeded' : 'Failed', () =>
          setTimeoutImpl(() => processLike.exit(exitCode || 0), mode === 'interactive' ? 1500 : 200));
      });
    });
  }

  function handleAgent(socket) {
    clients.add(socket);
    if (scrollback) safeSend(socket, scrollback);
    socket.on('message', raw => {
      let message;
      try { message = JSON.parse(raw.toString()); } catch { return; }
      if (message.type === 'input' && typeof message.data === 'string' && !exited) {
        term.write(message.data);
      } else if (message.type === 'resize' && message.cols > 0 && message.rows > 0 && !exited) {
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
