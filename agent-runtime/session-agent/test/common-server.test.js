'use strict';

const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const test = require('node:test');
const { Readable, Writable } = require('node:stream');

const commonDir = path.join(__dirname, '..', '..', 'common');
const { validateDriver } = require('../../common/driver-contract');
const { createCommonServer, MAX_BUFFER } = require('../../common/server');

class FakeTerminal {
  constructor() {
    this.dataHandlers = [];
    this.exitHandlers = [];
    this.writes = [];
    this.resizes = [];
    this.killed = false;
  }

  onData(handler) { this.dataHandlers.push(handler); }
  onExit(handler) { this.exitHandlers.push(handler); }
  write(data) { this.writes.push(data); }
  resize(cols, rows) { this.resizes.push([cols, rows]); }
  kill() { this.killed = true; }
  emitData(data) { for (const handler of this.dataHandlers) handler(data); }
  emitExit(event) { for (const handler of this.exitHandlers) handler(event); }
}

class FakeChildProcess {
  constructor() {
    this.stdinWrites = [];
    this.stdoutHandlers = [];
    this.stderrHandlers = [];
    this.handlers = {};
    this.killed = false;
    this.stdin = { write: (data, callback) => {
      this.stdinWrites.push(data);
      if (callback) queueMicrotask(() => callback(null));
      return true;
    }, on() {} };
    this.stdout = { on: (event, handler) => { if (event === 'data') this.stdoutHandlers.push(handler); } };
    this.stderr = { on: (event, handler) => { if (event === 'data') this.stderrHandlers.push(handler); } };
  }

  on(event, handler) { this.handlers[event] = handler; }
  kill() { this.killed = true; }
  emitStdout(text) { for (const handler of this.stdoutHandlers) handler(Buffer.from(text)); }
  emitStderr(text) { for (const handler of this.stderrHandlers) handler(Buffer.from(text)); }
  emitExit(code, signal) { if (this.handlers.exit) this.handlers.exit(code, signal); }
}

class FakeSocket {
  constructor() {
    this.OPEN = 1;
    this.readyState = 1;
    this.handlers = {};
    this.sent = [];
    this.closed = [];
  }

  on(event, handler) { this.handlers[event] = handler; }
  send(data) { this.sent.push(data); }
  close(code) { this.closed.push(code); }
  emit(event, data) { if (this.handlers[event]) this.handlers[event](data); }
}

function tick() {
  return new Promise(resolve => setImmediate(resolve));
}

function createHarness(environment = {}, driverOverrides = {}, harnessOptions = {}) {
  const terminals = [];
  const spawns = [];
  const children = [];
  const pipeSpawns = [];
  const requests = [];
  const commands = [];
  const pendingExec = [];
  const signals = {};
  const writes = [];
  const intervals = [];
  const exits = [];

  const fileCalls = [];
  class FakeWebSocketServer {
    constructor(options) {
      this.options = options;
      this.handlers = {};
    }
    on(event, handler) { this.handlers[event] = handler; }
    connect(socket, url) { this.handlers.connection(socket, { url }); }
  }

  class FakeHttpServer {
    constructor(handler) { this.handler = handler; this.port = null; }
    listen(port) { this.port = port; }
    request(request, response) { return this.handler(request, response); }
  }

  const http = {
    createServer(handler) { return new FakeHttpServer(handler); }
  };

  const materializer = {
    calls: [],
    materializeResult: [],
    async materialize(ids) { this.calls.push(ids); return this.materializeResult; }
  };

  const driver = {
    name: 'Test',
    stateDir: '.test-agent',
    authFilename: 'auth.json',
    attachmentCapabilities: Object.freeze({
      nativeImages: false, localImagePaths: true, mcpImages: true }),
    prepare() {},
    buildCommand: (_env, allowResume) => ({ cmd: 'test-agent', args: allowResume ? ['resume'] : ['fresh'] }),
    isResumeCommand: command => command.args.includes('resume'),
    isMissingResume: () => false,
    ...driverOverrides
  };
  const exists = new Set(['/workspace/repo']);
  const processLike = {
    env: {},
    on(signal, handler) { signals[signal] = handler; },
    exit(code) { exits.push(code); },
    kill: harnessOptions.kill
  };
  const runtime = createCommonServer({
    env: {
      AGENTHUB_PORT: '8123',
      AGENTHUB_MODE: 'interactive',
      AGENTHUB_HAS_REPO: '1',
      AGENTHUB_WORKDIR: '/workspace/repo',
      HOME: '/home/agent',
      ...environment
    },
    driver,
    dependencies: {
      pty: {
        spawn(cmd, args, options) {
          const terminal = new FakeTerminal();
          terminals.push(terminal);
          spawns.push({ cmd, args, options });
          return terminal;
        }
      },
      spawn(cmd, args, options) {
        const child = harnessOptions.createChild?.() ?? new FakeChildProcess();
        children.push(child);
        pipeSpawns.push({ cmd, args, options });
        return child;
      },
      WebSocketServer: FakeWebSocketServer,
      execFile(file, args, callback) {
        commands.push({ file, args });
        if (harnessOptions.deferExec) pendingExec.push(callback);
        else callback();
      },
      fs: {
        existsSync(file) { return exists.has(file); },
        writeFileSync(file, data) { writes.push({ file, data }); },
        readFileSync(file) {
          if (harnessOptions.files && file in harnessOptions.files) return harnessOptions.files[file];
          throw new Error('ENOENT: ' + file);
        }
      },
      fetch(url, options = {}) {
        requests.push({ url, options });
        return Promise.resolve(
          harnessOptions.fetchResponse?.(url, options) ?? { ok: true, text: async () => '' });
      },
      http,
      fileStore: {
        async put(id, name, readable, maxBytes) {
          const chunks = [];
          for await (const chunk of readable) chunks.push(chunk);
          fileCalls.push({ method: 'PUT', id, name, maxBytes, body: Buffer.concat(chunks).toString() });
          return { id, name, size: Buffer.concat(chunks).length };
        },
        async head(id) { fileCalls.push({ method: 'HEAD', id }); return null; },
        async open(id) { fileCalls.push({ method: 'GET', id }); return null; },
        async remove(id) { fileCalls.push({ method: 'DELETE', id }); }
      },
      process: processLike,
      setInterval(callback, ms) { intervals.push({ callback, ms }); return intervals.length; },
      attachmentMaterializer: materializer,
      setTimeout(callback) { callback(); return 1; },
      now: (() => { let value = 1000; return () => value += 100; })()
    }
  });

  return { runtime, driver, terminals, spawns, children, pipeSpawns, requests, commands, writes, intervals, exits, fileCalls, materializer, pendingExec, signals };
}

function requestHttp(harness, method, url, headers = {}, body = '') {
  const request = Readable.from(body ? [Buffer.from(body)] : []);
  request.method = method;
  request.url = url;
  request.headers = Object.fromEntries(Object.entries(headers).map(([key, value]) => [key.toLowerCase(), value]));
  return new Promise((resolve, reject) => {
    const chunks = [];
    const response = {
      statusCode: 200,
      headers: {},
      setHeader(name, value) { this.headers[name.toLowerCase()] = value; },
      write(chunk) { chunks.push(Buffer.from(chunk)); },
      end(chunk) {
        if (chunk) chunks.push(Buffer.from(chunk));
        resolve({ status: this.statusCode, headers: this.headers, body: Buffer.concat(chunks).toString() });
      }
    };
    Promise.resolve(harness.runtime.httpServer.request(request, response)).catch(reject);
  });
}

function createChatHarness(environment = {}, driverOverrides = {}, harnessOptions = {}) {
  return createHarness(environment, {
    buildCommand: (_env, allowResume) =>
      ({ cmd: 'test-agent', args: allowResume ? ['resume'] : ['fresh'], pipe: true }),
    ...driverOverrides
  }, harnessOptions);
}
test('common server attaches WebSockets and file HTTP routes to one listener', () => {
  const harness = createHarness();
  assert.equal(harness.runtime.webSocketServer.options.server, harness.runtime.httpServer);
  assert.equal(harness.runtime.httpServer.port, 8123);
});

test('file HTTP routes reject missing and incorrect tokens before touching storage', async () => {
  const harness = createHarness({ AGENTHUB_CALLBACK_TOKEN: 'correct-token' });
  const id = 'a'.repeat(32);

  const missing = await requestHttp(harness, 'PUT', `/agenthub/files/${id}`,
    { 'X-Agent-File-Name': 'shot.png' }, 'data');
  const wrong = await requestHttp(harness, 'PUT', `/agenthub/files/${id}`,
    { 'X-Agent-Token': 'wrong-token', 'X-Agent-File-Name': 'shot.png' }, 'data');

  assert.equal(missing.status, 401);
  assert.equal(wrong.status, 401);
  assert.deepEqual(harness.fileCalls, []);
});

test('file HTTP routes stream PUT and expose bounded metadata operations', async () => {
  const harness = createHarness({
    AGENTHUB_CALLBACK_TOKEN: 'correct-token',
    AGENTHUB_FILE_MAX_BYTES: '4'
  });
  const id = 'b'.repeat(32);
  const headers = {
    'X-Agent-Token': 'correct-token',
    'X-Agent-File-Name': 'shot.png'
  };

  const put = await requestHttp(harness, 'PUT', `/agenthub/files/${id}`, headers, 'data');
  assert.equal(put.status, 201);
  assert.deepEqual(JSON.parse(put.body), { id, name: 'shot.png', size: 4 });
  assert.deepEqual(harness.fileCalls[0], {
    method: 'PUT', id, name: 'shot.png', maxBytes: 4, body: 'data'
  });

  const head = await requestHttp(harness, 'HEAD', `/agenthub/files/${id}`, headers);
  assert.equal(head.status, 404);
  const deleted = await requestHttp(harness, 'DELETE', `/agenthub/files/${id}`, headers);
  assert.equal(deleted.status, 204);
  assert.deepEqual(harness.fileCalls.slice(1), [
    { method: 'HEAD', id },
    { method: 'DELETE', id }
  ]);
});


test('common transport validates every required driver export', () => {
  const valid = {
    name: 'Example', stateDir: '.example', authFilename: 'auth.json',
    attachmentCapabilities: {
      nativeImages: false, localImagePaths: true, mcpImages: true },
    buildCommand() {}, isResumeCommand() {}, isMissingResume() {}, prepare() {}
  };
  assert.equal(validateDriver(valid), valid);

  for (const key of ['name', 'stateDir', 'authFilename', 'attachmentCapabilities',
    'buildCommand', 'isResumeCommand', 'isMissingResume', 'prepare']) {
    const invalid = { ...valid };
    delete invalid[key];
    assert.throws(() => validateDriver(invalid), new RegExp(`missing ${key}`, 'i'));
  }
  assert.throws(() => validateDriver({ ...valid, name: '' }), /missing name/i);
});

test('common transport accepts only safe single relative archive names', () => {
  const valid = {
    name: 'Example', stateDir: '.claude', authFilename: '.credentials.json',
    attachmentCapabilities: {
      nativeImages: false, localImagePaths: true, mcpImages: true },
    buildCommand() {}, isResumeCommand() {}, isMissingResume() {}, prepare() {}
  };

  for (const [stateDir, authFilename] of [
    ['.claude', '.credentials.json'],
    ['.codex', 'auth.json']
  ]) {
    assert.doesNotThrow(() => validateDriver({ ...valid, stateDir, authFilename }));
  }

  for (const [field, value] of [
    ['stateDir', ''],
    ['stateDir', '.'],
    ['stateDir', '..'],
    ['stateDir', '../escape'],
    ['stateDir', 'nested/path'],
    ['stateDir', 'nested\\path'],
    ['stateDir', '"quoted"'],
    ['stateDir', 'bad name'],
    ['stateDir', 'bad;name'],
    ['authFilename', ''],
    ['authFilename', '.'],
    ['authFilename', '..'],
    ['authFilename', '../auth.json'],
    ['authFilename', 'nested/auth.json'],
    ['authFilename', 'nested\\auth.json'],
    ['authFilename', "'quoted'"],
    ['authFilename', 'bad\nname'],
    ['authFilename', '$HOME']
  ]) {
    assert.throws(() => validateDriver({ ...valid, [field]: value }),
      new RegExp(field + ' must be a safe single relative name', 'i'));
  }
});

test('common transport accepts optional safe stateExcludes paths and globs', () => {
  const valid = {
    name: 'Example', stateDir: '.openclaw', authFilename: 'auth-profiles.json',
    attachmentCapabilities: {
      nativeImages: false, localImagePaths: true, mcpImages: true },
    buildCommand() {}, isResumeCommand() {}, isMissingResume() {}, prepare() {}
  };

  assert.doesNotThrow(() => validateDriver({
    ...valid,
    stateExcludes: [
      '.openclaw/agents/main/agent/auth-profiles.json',
      '.openclaw/agents/*/agent/openclaw-agent.sqlite'
    ]
  }));
  assert.doesNotThrow(() => validateDriver({ ...valid, stateExcludes: [] }));
  assert.doesNotThrow(() => validateDriver(valid));

  for (const value of [
    null,
    '.openclaw/agents/../escape/auth.json',
    '/absolute/path',
    'nested\\windows',
    'bad;name',
    'bad name',
    '$HOME/secret',
    'path with spaces',
    ['ok', '../bad']
  ]) {
    assert.throws(() => validateDriver({
      ...valid,
      stateExcludes: Array.isArray(value) ? value : [value]
    }), /stateExcludes/i);
  }
  assert.throws(() => validateDriver({ ...valid, stateExcludes: 'not-an-array' }),
    /stateExcludes/i);
});

test('common transport is free of provider-specific command and state knowledge', () => {
  const source = fs.readFileSync(path.join(commonDir, 'server.js'), 'utf8');
  assert.doesNotMatch(source, /claude|codex|--resume|No conversation found|\.credentials\.json/i);
});

test('common transport caps scrollback and replays it on WebSocket connect', () => {
  const harness = createHarness();
  const output = `discard${'x'.repeat(MAX_BUFFER)}`;
  harness.terminals[0].emitData(output);

  const socket = new FakeSocket();
  harness.runtime.webSocketServer.connect(socket, '/?token=ignored');

  assert.deepEqual(socket.sent, ['x'.repeat(MAX_BUFFER)]);
  socket.emit('message', Buffer.from(JSON.stringify({ type: 'input', data: 'hello' })));
  socket.emit('message', Buffer.from(JSON.stringify({ type: 'resize', cols: 90, rows: 20 })));
  assert.deepEqual(harness.terminals[0].writes, ['hello']);
  assert.deepEqual(harness.terminals[0].resizes, [[90, 20]]);
});

test('a resumed session replays the history the hub kept, ahead of its own output', async () => {
  const harness = createHarness(
    {
      AGENTHUB_RESUME: '1',
      AGENTHUB_CALLBACK_URL: 'https://hub.invalid/internal/sessions/s1',
      AGENTHUB_CALLBACK_TOKEN: 'agent-token'
    },
    {},
    {
      fetchResponse: url => url.endsWith('/scrollback')
        ? { ok: true, text: async () => 'conversation before the resume' }
        : { ok: true, text: async () => '' }
    }
  );
  await tick();

  const restore = harness.requests.find(request => request.url.endsWith('/scrollback'));
  assert.ok(restore, 'the agent should ask the hub for the history it kept');
  assert.equal(restore.options.headers['X-Agent-Token'], 'agent-token');
  assert.notEqual(restore.options.method, 'PUT');

  harness.terminals[0].emitData(' and after it');
  const socket = new FakeSocket();
  harness.runtime.webSocketServer.connect(socket, '/?token=ignored');

  assert.deepEqual(socket.sent, ['conversation before the resume and after it']);
});

test('a resumed session still starts when the history cannot be fetched', async () => {
  const harness = createHarness(
    {
      AGENTHUB_RESUME: '1',
      AGENTHUB_CALLBACK_URL: 'https://hub.invalid/internal/sessions/s1',
      AGENTHUB_CALLBACK_TOKEN: 'agent-token'
    },
    {},
    {
      fetchResponse: url => url.endsWith('/scrollback')
        ? { ok: false, status: 503, text: async () => '' }
        : { ok: true, text: async () => '' }
    }
  );
  await tick();

  // Losing the history is bad; refusing to start the session over it would be worse.
  assert.equal(harness.terminals.length, 1);
  harness.terminals[0].emitData('fresh output');
  const socket = new FakeSocket();
  harness.runtime.webSocketServer.connect(socket, '/?token=ignored');
  assert.deepEqual(socket.sent, ['fresh output']);
});

test('a fresh session starts without waiting on the hub', () => {
  const harness = createHarness({
    AGENTHUB_CALLBACK_URL: 'https://hub.invalid/internal/sessions/s1',
    AGENTHUB_CALLBACK_TOKEN: 'agent-token'
  });

  // No resume, so the agent must be up synchronously and ask for nothing.
  assert.equal(harness.terminals.length, 1);
  assert.equal(harness.requests.filter(r => r.url.endsWith('/scrollback')).length, 0);
});

test('common transport routes /shell to a login shell in the selected working directory', () => {
  const harness = createHarness();
  const socket = new FakeSocket();
  harness.runtime.webSocketServer.connect(socket, '/shell?token=ignored');

  assert.deepEqual(harness.spawns[1], {
    cmd: 'bash', args: ['-l'],
    options: {
      name: 'xterm-256color', cols: 120, rows: 32,
      cwd: '/workspace/repo', env: harness.runtime.env
    }
  });
  socket.emit('close');
  assert.equal(harness.terminals[1].killed, true);
});

test('common transport archives driver state without its subscription credentials', async () => {
  const harness = createHarness({
    AGENTHUB_STATE_PUT_URL: 'https://storage.invalid/state',
    AGENTHUB_SCROLLBACK_PUT_URL: 'https://storage.invalid/scrollback',
    AGENTHUB_S3_INSECURE: '1'
  });
  harness.terminals[0].emitData('saved output');
  harness.intervals[0].callback();
  await tick();

  assert.deepEqual(harness.writes, [{ file: '/tmp/scrollback.log', data: 'saved output' }]);
  assert.equal(harness.commands.length, 2);
  assert.match(harness.commands[0].args[1], /curl -fsS --max-time 120 -k -T \/tmp\/scrollback\.log/);
  assert.match(harness.commands[1].args[1], /tar czf \/tmp\/state\.tgz/);
  assert.match(harness.commands[1].args[1], /"\.test-agent"/);
  assert.match(harness.commands[1].args[1], /--exclude="\.test-agent\/auth\.json"/);
  assert.doesNotMatch(harness.commands[1].args[1], /cat |credentials\.json/);
});

test('common transport applies optional stateExcludes as extra tar excludes', () => {
  const harness = createHarness({ AGENTHUB_STATE_PUT_URL: 'https://storage.invalid/state' }, {
    name: 'OpenClaw',
    stateDir: '.openclaw',
    authFilename: 'auth-profiles.json',
    stateExcludes: [
      '.openclaw/agents/main/agent/auth-profiles.json',
      '.openclaw/agents/*/agent/openclaw-agent.sqlite'
    ]
  });
  harness.intervals[0].callback();
  assert.equal(harness.commands.length, 1);
  const command = harness.commands[0].args[1];
  assert.match(command, /--exclude="\.openclaw\/auth-profiles\.json"/);
  assert.match(command, /--exclude="\.openclaw\/agents\/main\/agent\/auth-profiles\.json"/);
  assert.match(command, /--exclude="\.openclaw\/agents\/\*\/agent\/openclaw-agent\.sqlite"/);
});

test('common transport keeps the last archive unless tar finished, accepting files changed while read', () => {
  const harness = createHarness({ AGENTHUB_STATE_PUT_URL: 'https://storage.invalid/state' });
  harness.intervals[0].callback();
  const command = harness.commands[0].args[1];
  assert.match(command, /^nice -n 10 timeout 120 tar czf \/tmp\/state\.tgz\.part /);
  assert.match(command, /if \[ \$\? -le 1 \]; then mv -f \/tmp\/state\.tgz\.part \/tmp\/state\.tgz && curl -fsS --max-time 120 -T \/tmp\/state\.tgz/);
  assert.match(command, /else rm -f \/tmp\/state\.tgz\.part; exit 1; fi$/);
});

test('common transport skips a persistence tick while the previous run is still archiving', () => {
  const harness = createHarness({ AGENTHUB_STATE_PUT_URL: 'https://storage.invalid/state' },
    {}, { deferExec: true });
  harness.intervals[0].callback();
  harness.intervals[0].callback();
  harness.intervals[0].callback();
  assert.equal(harness.commands.length, 1);

  harness.pendingExec.shift()();
  harness.intervals[0].callback();
  assert.equal(harness.commands.length, 2);
});

test('common transport runs one final persistence after an in-flight run on SIGTERM', () => {
  const harness = createHarness({ AGENTHUB_STATE_PUT_URL: 'https://storage.invalid/state' },
    {}, { deferExec: true });
  harness.intervals[0].callback();
  harness.signals.SIGTERM();
  harness.signals.SIGTERM();
  assert.equal(harness.commands.length, 1);
  assert.deepEqual(harness.exits, []);

  harness.pendingExec.shift()();
  assert.equal(harness.commands.length, 2, 'the state written since the tick is archived once more');
  assert.deepEqual(harness.exits, []);

  harness.pendingExec.shift()();
  assert.deepEqual(harness.exits, [0, 0]);
  assert.equal(harness.commands.length, 2);
});

function watcherHarness(probesUntilGone, extra = {}) {
  const events = [];
  let probes = 0;
  const harness = createHarness({}, {}, {
    files: { '/tmp/agenthub-auth-watcher.pid': '4242\n' },
    kill(pid, signal) {
      events.push([pid, signal]);
      if (signal === 0 && ++probes >= probesUntilGone) throw Object.assign(new Error('gone'), { code: 'ESRCH' });
    },
    ...extra
  });
  return { harness, events };
}

test('common transport lets the credential watcher flush before it exits on SIGTERM', () => {
  const { harness, events } = watcherHarness(3);
  harness.signals.SIGTERM();

  assert.deepEqual(events[0], [4242, 'SIGTERM']);
  assert.deepEqual(events.slice(1), [[4242, 0], [4242, 0], [4242, 0]]);
  assert.deepEqual(harness.exits, [0]);
});

test('common transport stops waiting for a credential watcher that never exits', () => {
  const { harness, events } = watcherHarness(Infinity);
  harness.signals.SIGTERM();

  assert.deepEqual(harness.exits, [0]);
  // 6s budget at the harness clock's 100ms per reading: bounded, not a hang.
  assert.ok(events.length > 1 && events.length < 100, String(events.length));
});

test('common transport flushes the credential watcher when the agent itself ends', () => {
  const { harness, events } = watcherHarness(1);
  harness.terminals[0].emitExit({ exitCode: 0, signal: 0 });

  assert.deepEqual(events, [[4242, 'SIGTERM'], [4242, 0]]);
  assert.deepEqual(harness.exits, [0]);
});

test('common transport exits normally when no credential watcher was started', () => {
  const harness = createHarness({}, {}, { kill() { throw new Error('must not signal'); } });
  harness.signals.SIGTERM();
  assert.deepEqual(harness.exits, [0]);
});

test('common transport backs up scrollback and posts Running and terminal status', async () => {
  const harness = createHarness({
    AGENTHUB_CALLBACK_URL: 'https://backend.invalid/internal/session',
    AGENTHUB_CALLBACK_TOKEN: 'synthetic-callback-token'
  });
  harness.terminals[0].emitData('completed output');
  harness.terminals[0].emitExit({ exitCode: 0, signal: 0 });
  await tick();
  await tick();

  assert.deepEqual(harness.requests.map(request => [request.url, request.options.method]), [
    ['https://backend.invalid/internal/session/status', 'POST'],
    ['https://backend.invalid/internal/session/scrollback', 'PUT'],
    ['https://backend.invalid/internal/session/status', 'POST']
  ]);
  assert.equal(harness.requests[0].options.body, JSON.stringify({ status: 'Running' }));
  const newline = String.fromCharCode(13, 10);
  assert.equal(harness.requests[1].options.body,
    'completed output' + newline + '[agent] Session ended (code 0).' + newline);
  assert.equal(harness.requests[2].options.body, JSON.stringify({ status: 'Succeeded' }));
  assert.deepEqual(harness.exits, [0]);
});

test('common transport reports pod resource usage with the persistence heartbeat', () => {
  const harness = createHarness({
    AGENTHUB_CALLBACK_URL: 'https://backend.invalid/internal/session',
    AGENTHUB_CALLBACK_TOKEN: 'synthetic-callback-token'
  }, {}, {
    files: {
      '/sys/fs/cgroup/cpu.stat': 'usage_usec 2500000\nuser_usec 2000000\nsystem_usec 500000\n',
      '/sys/fs/cgroup/memory.current': '104857600\n',
      '/proc/net/dev': 'Inter-|   Receive                |  Transmit\n' +
        ' face |bytes packets errs drop fifo frame compressed multicast|bytes packets errs drop fifo colls carrier compressed\n' +
        '    lo:     999      9    0    0    0     0          0         0      999       9    0    0    0     0       0          0\n' +
        '  eth0:    1000     10    0    0    0     0          0         0     2000      20    0    0    0     0       0          0\n'
    }
  });
  harness.intervals[0].callback(); // the 30s persistence tick

  const post = harness.requests.find(request => request.url.endsWith('/resources'));
  assert.ok(post, 'expected a POST to /resources');
  assert.equal(post.options.method, 'POST');
  assert.equal(post.options.headers['X-Agent-Token'], 'synthetic-callback-token');
  const body = JSON.parse(post.options.body);
  assert.equal(body.cpuSeconds, 2.5);          // usage_usec -> seconds
  assert.equal(body.memoryBytes, 104857600);
  assert.equal(body.rxBytes, 1000);            // eth0 only; loopback is not traffic
  assert.equal(body.txBytes, 2000);
});

test('common transport skips the resource report when no source is readable', () => {
  const harness = createHarness({
    AGENTHUB_CALLBACK_URL: 'https://backend.invalid/internal/session',
    AGENTHUB_CALLBACK_TOKEN: 'synthetic-callback-token'
  });
  harness.intervals[0].callback();
  assert.equal(harness.requests.filter(request => request.url.endsWith('/resources')).length, 0);
});

test('common transport falls back to cgroup v1 resource files', () => {
  const harness = createHarness({
    AGENTHUB_CALLBACK_URL: 'https://backend.invalid/internal/session',
    AGENTHUB_CALLBACK_TOKEN: 'synthetic-callback-token'
  }, {}, {
    files: {
      '/sys/fs/cgroup/cpuacct/cpuacct.usage': '3000000000\n', // ns
      '/sys/fs/cgroup/memory/memory.usage_in_bytes': '52428800\n'
    }
  });
  harness.intervals[0].callback();

  const post = harness.requests.find(request => request.url.endsWith('/resources'));
  assert.ok(post);
  const body = JSON.parse(post.options.body);
  assert.equal(body.cpuSeconds, 3);
  assert.equal(body.memoryBytes, 52428800);
  assert.equal(body.rxBytes, 0);
  assert.equal(body.txBytes, 0);
});

test('common transport retries a missing resume once and then launches fresh', () => {
  const checks = [];
  const harness = createHarness({}, {
    isMissingResume(output, exitCode, elapsedMs) {
      checks.push({ output, exitCode, elapsedMs });
      return output.includes('missing') && exitCode === 1;
    }
  });
  harness.terminals[0].emitData('missing state');
  harness.terminals[0].emitExit({ exitCode: 1, signal: 0 });

  assert.equal(harness.terminals.length, 2);
  assert.deepEqual(harness.spawns.map(spawn => spawn.args), [['resume'], ['fresh']]);
  const socket = new FakeSocket();
  harness.runtime.webSocketServer.connect(socket, '/');
  assert.match(socket.sent[0], /No saved conversation to resume — starting fresh\./);
  assert.equal(checks.length, 1);
});

test('Codex device-auth resume retries fresh exactly once without repeating login', () => {
  const codexDriver = require('../../codex/driver');
  const harness = createHarness({
    AGENTHUB_CODEX_DEVICE_AUTH: '1',
    AGENTHUB_RESUME: '1',
    AGENTHUB_STATE_RESTORED: '1',
    CODEX_HOME: '/home/agent/.codex'
  }, codexDriver);

  const wrapper = path.join(__dirname, '..', '..', 'codex', 'device-login.sh');
  assert.deepEqual(harness.spawns[0].args, [wrapper, '--no-alt-screen', '--no-daemon', 'resume', '--last']);
  harness.terminals[0].emitData('No saved session found to resume');
  harness.terminals[0].emitExit({ exitCode: 1, signal: 0 });

  assert.equal(harness.terminals.length, 2);
  assert.deepEqual(harness.spawns[1].args, [wrapper, '--no-alt-screen', '--no-daemon']);
  harness.terminals[1].emitData('No saved session found to resume');
  harness.terminals[1].emitExit({ exitCode: 1, signal: 0 });
  assert.equal(harness.terminals.length, 2);
});

test('common transport does not infer resume merely because the first launch allows it', () => {
  let missingResumeChecks = 0;
  const harness = createHarness({}, {
    isResumeCommand: () => false,
    isMissingResume: () => {
      missingResumeChecks++;
      return true;
    }
  });

  harness.terminals[0].emitExit({ exitCode: 1, signal: 0 });

  assert.equal(harness.terminals.length, 1);
  assert.equal(missingResumeChecks, 0);
  assert.deepEqual(harness.exits, [1]);
});

test('common transport scopes a Codex API key to agent child and keeps parent and shell clean', () => {
  const harness = createHarness({ CODEX_API_KEY: 'synthetic-api-key' }, {
    prepare(env) {
      const key = env.CODEX_API_KEY;
      delete env.CODEX_API_KEY;
      return { childEnv: { CODEX_API_KEY: key } };
    }
  });
  assert.equal(harness.runtime.env.CODEX_API_KEY, undefined);
  assert.equal(harness.spawns[0].options.env.CODEX_API_KEY, 'synthetic-api-key');
  const socket = new FakeSocket();
  harness.runtime.webSocketServer.connect(socket, '/shell');
  assert.equal(harness.spawns[1].options.env.CODEX_API_KEY, undefined);
});

test('common transport scopes a Claude API key to agent child and keeps parent and shell clean', () => {
  const claudeDriver = require('../../claude/driver');
  const harness = createHarness(
    { ANTHROPIC_API_KEY: 'synthetic-claude-key' },
    claudeDriver
  );
  assert.equal(harness.runtime.env.ANTHROPIC_API_KEY, undefined);
  assert.equal(harness.spawns[0].options.env.ANTHROPIC_API_KEY, 'synthetic-claude-key');
  const socket = new FakeSocket();
  harness.runtime.webSocketServer.connect(socket, '/shell');
  assert.equal(harness.spawns[1].options.env.ANTHROPIC_API_KEY, undefined);
});

test('chat transport spawns a pipe instead of a PTY and replays only durable events', () => {
  const harness = createChatHarness();
  assert.equal(harness.terminals.length, 0);
  assert.equal(harness.pipeSpawns.length, 1);
  assert.deepEqual(harness.pipeSpawns[0].options.stdio, ['pipe', 'pipe', 'pipe']);
  assert.equal(harness.pipeSpawns[0].options.cwd, '/workspace/repo');

  const live = new FakeSocket();
  harness.runtime.webSocketServer.connect(live, '/');

  const child = harness.children[0];
  child.emitStdout('{"type":"stream_event","event":{"type":"content_block_delta"}}\n');
  child.emitStdout('{"type":"assistant","message":{"role":"assistant"}}\npartial');
  child.emitStdout(' tail{"garbage"\n');

  assert.deepEqual(live.sent, [
    '{"type":"stream_event","event":{"type":"content_block_delta"}}\n',
    '{"type":"assistant","message":{"role":"assistant"}}\n',
    'partial tail{"garbage"\n'
  ]);

  const replay = new FakeSocket();
  harness.runtime.webSocketServer.connect(replay, '/');
  assert.deepEqual(replay.sent, [
    '{"type":"assistant","message":{"role":"assistant"}}\n' +
    'partial tail{"garbage"\n'
  ]);
});

test('chat transport forwards user input as stream-json and echoes it durably', async () => {
  const harness = createChatHarness();
  const socket = new FakeSocket();
  harness.runtime.webSocketServer.connect(socket, '/');

  socket.emit('message', Buffer.from(JSON.stringify({ type: 'chat', text: 'hello agent' })));
  socket.emit('message', Buffer.from(JSON.stringify({ type: 'chat', text: '   ' })));
  socket.emit('message', Buffer.from(JSON.stringify({ type: 'input', data: 'raw keys' })));
  socket.emit('message', Buffer.from(JSON.stringify({ type: 'resize', cols: 90, rows: 20 })));
  await tick();
  await tick();

  const child = harness.children[0];
  assert.deepEqual(child.stdinWrites, [
    JSON.stringify({ type: 'user', message: { role: 'user', content: [{ type: 'text', text: 'hello agent' }] } }) + '\n'
  ]);
  const echo = JSON.stringify({
    type: 'user',
    message: { role: 'user', content: [{ type: 'text', text: 'hello agent' }] },
    agenthub_echo: true
  }) + '\n';
  assert.deepEqual(socket.sent, [echo]);

  const replay = new FakeSocket();
  harness.runtime.webSocketServer.connect(replay, '/');
  assert.deepEqual(replay.sent, [echo]);
});

test('chat materializes ready IDs and echoes metadata without local paths', async () => {
  const harness = createChatHarness();
  const id = 'a'.repeat(32);
  harness.materializer.materializeResult = [{
    id, name: 'shot.png', mimeType: 'image/png', size: 12,
    localPath: '/workspace/.agenthub/files/' + id + '/shot.png'
  }];
  const socket = new FakeSocket();
  harness.runtime.webSocketServer.connect(socket, '/');

  socket.emit('message', Buffer.from(JSON.stringify({
    type: 'chat', text: 'inspect', attachments: [id], clientTurnId: 'turn-1'
  })));
  await tick();
  await tick();

  assert.deepEqual(harness.materializer.calls, [[id]]);
  assert.match(harness.children[0].stdinWrites[0], /inspect each attached image/i);
  assert.match(harness.children[0].stdinWrites[0], /shot\.png/);
  assert.doesNotMatch(socket.sent.join(''), /workspace|localPath/);
  const echo = JSON.parse(socket.sent[0]);
  assert.deepEqual(echo.attachments, [{
    id, name: 'shot.png', mimeType: 'image/png', size: 12,
    visualDelivery: 'localImagePaths'
  }]);
  assert.match(socket.sent.join(''), /"subtype":"chat_delivered".*"clientTurnId":"turn-1"/);
});

test('chat reports delivery failure when agent stdin rejects the write', async () => {
  const harness = createChatHarness({}, {}, {
    createChild() {
      const child = new FakeChildProcess();
      child.stdin = new Writable({
        write(_chunk, _encoding, callback) { callback(new Error('closed')); }
      });
      return child;
    }
  });
  const socket = new FakeSocket();
  harness.runtime.webSocketServer.connect(socket, '/');

  socket.emit('message', Buffer.from(JSON.stringify({
    type: 'chat', text: 'retain me', clientTurnId: 'turn-failed'
  })));
  await tick();
  await tick();

  assert.match(socket.sent.join(''), /attachment_delivery_failed/);
  assert.match(socket.sent.join(''), /"clientTurnId":"turn-failed"/);
  assert.doesNotMatch(socket.sent.join(''), /chat_delivered/);
  assert.doesNotMatch(socket.sent.join(''), /retain me/);
  assert.deepEqual(harness.exits, []);

  const secondSocket = new FakeSocket();
  harness.runtime.webSocketServer.connect(secondSocket, '/');
  assert.deepEqual(secondSocket.closed, []);
});

test('chat rejects an attachment turn before stdin when no visual delivery exists', async () => {
  const harness = createChatHarness({}, {
    attachmentCapabilities: Object.freeze({
      nativeImages: false, localImagePaths: false, mcpImages: false
    })
  });
  const id = 'b'.repeat(32);
  harness.materializer.materializeResult = [{
    id, name: 'shot.png', mimeType: 'image/png', size: 12,
    localPath: '/workspace/.agenthub/files/' + id + '/shot.png'
  }];
  const socket = new FakeSocket();
  harness.runtime.webSocketServer.connect(socket, '/');

  socket.emit('message', Buffer.from(JSON.stringify({
    type: 'chat', text: 'inspect', attachments: [id], clientTurnId: 'turn-2'
  })));
  await tick();
  await tick();

  assert.deepEqual(harness.children[0].stdinWrites, []);
  assert.match(socket.sent.join(''), /attachment_delivery_failed/);
  assert.match(socket.sent.join(''), /"clientTurnId":"turn-2"/);
});
test('chat transport sends an interrupt control request on demand', () => {
  const harness = createChatHarness();
  const socket = new FakeSocket();
  harness.runtime.webSocketServer.connect(socket, '/');

  socket.emit('message', Buffer.from(JSON.stringify({ type: 'interrupt' })));
  socket.emit('message', Buffer.from(JSON.stringify({ type: 'interrupt' })));

  assert.deepEqual(harness.children[0].stdinWrites, [
    JSON.stringify({ type: 'control_request', request_id: 'agenthub-1', request: { subtype: 'interrupt' } }) + '\n',
    JSON.stringify({ type: 'control_request', request_id: 'agenthub-2', request: { subtype: 'interrupt' } }) + '\n'
  ]);
});

test('chat transport sends the initial prompt once on fresh starts only', () => {
  const fresh = createChatHarness({ AGENTHUB_PROMPT: 'do the task' }, {
    buildCommand: () => ({ cmd: 'test-agent', args: ['fresh'], pipe: true })
  });
  assert.deepEqual(fresh.children[0].stdinWrites, [
    JSON.stringify({ type: 'user', message: { role: 'user', content: [{ type: 'text', text: 'do the task' }] } }) + '\n'
  ]);

  const resumed = createChatHarness({ AGENTHUB_PROMPT: 'do the task' }, {
    isResumeCommand: () => true
  });
  assert.deepEqual(resumed.children[0].stdinWrites, []);
});

test('chat transport wraps stderr and exit as durable agenthub events', async () => {
  const harness = createChatHarness({
    AGENTHUB_CALLBACK_URL: 'https://backend.invalid/internal/session',
    AGENTHUB_CALLBACK_TOKEN: 'synthetic-callback-token'
  });
  const socket = new FakeSocket();
  harness.runtime.webSocketServer.connect(socket, '/');

  const child = harness.children[0];
  child.emitStderr('boom');
  child.emitExit(1, null);
  await tick();
  await tick();

  assert.deepEqual(socket.sent, [
    JSON.stringify({ type: 'agenthub', subtype: 'stderr', text: 'boom' }) + '\n',
    JSON.stringify({ type: 'agenthub', subtype: 'exit', code: 1, signal: null }) + '\n'
  ]);
  assert.deepEqual(socket.closed, [1000]);
  assert.equal(harness.requests.at(-1).options.body, JSON.stringify({ status: 'Failed' }));
  assert.deepEqual(harness.exits, [1]);
});

test('chat transport retries a missing resume once with a durable info event', () => {
  const harness = createChatHarness({}, {
    isMissingResume: (output, exitCode) => exitCode === 1 && output.includes('missing')
  });
  harness.children[0].emitStderr('missing state');
  harness.children[0].emitExit(1, null);

  assert.equal(harness.children.length, 2);
  assert.deepEqual(harness.pipeSpawns.map(spawn => spawn.args), [['resume'], ['fresh']]);
  const socket = new FakeSocket();
  harness.runtime.webSocketServer.connect(socket, '/');
  assert.equal(socket.sent[0].split('\n')[0],
    JSON.stringify({ type: 'agenthub', subtype: 'stderr', text: 'missing state' }));
  assert.match(socket.sent[0],
    /"subtype":"info","text":"No saved conversation to resume — starting fresh\."/);
});

test('common transport production archive includes Codex state but excludes auth', () => {
  const harness = createHarness({ AGENTHUB_STATE_PUT_URL: 'https://storage.invalid/codex-state' }, {
    name: 'Codex', stateDir: '.codex', authFilename: 'auth.json'
  });
  harness.intervals[0].callback();
  assert.equal(harness.commands.length, 1);
  assert.match(harness.commands[0].args[1], /"\.codex"/);
  assert.match(harness.commands[0].args[1], /--exclude="\.codex\/auth\.json"/);
});
