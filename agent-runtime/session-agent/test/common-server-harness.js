'use strict';

// The fake transport every common-server test runs against: PTY, child process, sockets, HTTP,
// timers and the hub, all recorded. Shared by common-server.test.js and the message-route tests.

const path = require('node:path');
const { Readable } = require('node:stream');

const commonDir = path.join(__dirname, '..', '..', 'common');
const { createCommonServer } = require('../../common/server');

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
  const renames = [];
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
        mkdirSync() {},
        renameSync(from, to) { renames.push({ from, to }); },
        readFileSync(file) {
          if (harnessOptions.files && file in harnessOptions.files) return harnessOptions.files[file];
          throw new Error('ENOENT: ' + file);
        },
        statSync(file) {
          if (harnessOptions.files && file in harnessOptions.files) {
            return { size: harnessOptions.files[file].length, mtimeMs: harnessOptions.mtimeMs || 1 };
          }
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
      setTimeout(callback, ms) {
        if (harnessOptions.setTimeout) return harnessOptions.setTimeout(callback, ms);
        callback();
        return 1;
      },
      now: harnessOptions.now || (() => { let value = 1000; return () => value += 100; })()
    }
  });

  return { runtime, driver, terminals, spawns, children, pipeSpawns, requests, commands, writes, renames, intervals, exits, fileCalls, materializer, pendingExec, signals };
}

function requestHttp(harness, method, url, headers = {}, body = '', remoteAddress = '10.0.0.9') {
  const request = Readable.from(body ? [Buffer.from(body)] : []);
  request.method = method;
  request.url = url;
  request.headers = Object.fromEntries(Object.entries(headers).map(([key, value]) => [key.toLowerCase(), value]));
  request.socket = { remoteAddress };
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

module.exports = { FakeTerminal, FakeChildProcess, FakeSocket, tick, createHarness, requestHttp, createChatHarness, commonDir };
