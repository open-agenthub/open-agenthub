'use strict';

const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const test = require('node:test');

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

function createHarness(environment = {}, driverOverrides = {}) {
  const terminals = [];
  const spawns = [];
  const requests = [];
  const commands = [];
  const writes = [];
  const intervals = [];
  const exits = [];

  class FakeWebSocketServer {
    constructor(options) {
      this.options = options;
      this.handlers = {};
    }
    on(event, handler) { this.handlers[event] = handler; }
    connect(socket, url) { this.handlers.connection(socket, { url }); }
  }

  const driver = {
    name: 'Test',
    stateDir: '.test-agent',
    authFilename: 'auth.json',
    prepare() {},
    buildCommand: (_env, allowResume) => ({ cmd: 'test-agent', args: allowResume ? ['resume'] : ['fresh'] }),
    isResumeCommand: command => command.args.includes('resume'),
    isMissingResume: () => false,
    ...driverOverrides
  };
  const exists = new Set(['/workspace/repo']);
  const processLike = {
    env: {},
    on() {},
    exit(code) { exits.push(code); }
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
      WebSocketServer: FakeWebSocketServer,
      execFile(file, args, callback) {
        commands.push({ file, args });
        callback();
      },
      fs: {
        existsSync(file) { return exists.has(file); },
        writeFileSync(file, data) { writes.push({ file, data }); }
      },
      fetch(url, options = {}) {
        requests.push({ url, options });
        return Promise.resolve({ ok: true });
      },
      process: processLike,
      setInterval(callback, ms) { intervals.push({ callback, ms }); return intervals.length; },
      setTimeout(callback) { callback(); return 1; },
      now: (() => { let value = 1000; return () => value += 100; })()
    }
  });

  return { runtime, driver, terminals, spawns, requests, commands, writes, intervals, exits };
}

test('common transport validates every required driver export', () => {
  const valid = {
    name: 'Example', stateDir: '.example', authFilename: 'auth.json',
    buildCommand() {}, isResumeCommand() {}, isMissingResume() {}, prepare() {}
  };
  assert.equal(validateDriver(valid), valid);

  for (const key of ['name', 'stateDir', 'authFilename', 'buildCommand', 'isResumeCommand',
    'isMissingResume', 'prepare']) {
    const invalid = { ...valid };
    delete invalid[key];
    assert.throws(() => validateDriver(invalid), new RegExp(`missing ${key}`, 'i'));
  }
  assert.throws(() => validateDriver({ ...valid, name: '' }), /missing name/i);
});

test('common transport accepts only safe single relative archive names', () => {
  const valid = {
    name: 'Example', stateDir: '.claude', authFilename: '.credentials.json',
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
  assert.match(harness.commands[0].args[1], /curl -fsS -k -T \/tmp\/scrollback\.log/);
  assert.match(harness.commands[1].args[1], /tar czf \/tmp\/state\.tgz/);
  assert.match(harness.commands[1].args[1], /"\.test-agent"/);
  assert.match(harness.commands[1].args[1], /--exclude="\.test-agent\/auth\.json"/);
  assert.doesNotMatch(harness.commands[1].args[1], /cat |credentials\.json/);
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
