'use strict';

const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const test = require('node:test');
const { Writable } = require('node:stream');

const { validateDriver } = require('../../common/driver-contract');
const { MAX_BUFFER } = require('../../common/server');
const { waitForExport } = require('./sign-in-test-wait');
const { FakeChildProcess, FakeSocket, tick, createHarness, requestHttp, createChatHarness, commonDir } = require('./common-server-harness');

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

test('file GET passes one byte range to the store and answers 416 past the end', async () => {
  const harness = createHarness({ AGENTHUB_CALLBACK_TOKEN: 'correct-token' }, {}, {
    openFile: (id, range) => range?.start === 4000 ? { name: 'demo.mp4', size: 3000, unsatisfiable: true } : null
  });
  const id = 'c'.repeat(32);
  const headers = { 'X-Agent-Token': 'correct-token' };

  await requestHttp(harness, 'GET', `/agenthub/files/${id}`, { ...headers, Range: 'bytes=100-199' });
  await requestHttp(harness, 'GET', `/agenthub/files/${id}`, { ...headers, Range: 'bytes=-50' });
  // Several ranges, or a malformed one, are served in full rather than refused.
  await requestHttp(harness, 'GET', `/agenthub/files/${id}`, { ...headers, Range: 'bytes=0-1,5-6' });
  const past = await requestHttp(harness, 'GET', `/agenthub/files/${id}`, { ...headers, Range: 'bytes=4000-' });

  assert.deepEqual(harness.fileCalls, [
    { method: 'GET', id, range: { start: 100, end: 199 } },
    { method: 'GET', id, range: { suffix: 50 } },
    { method: 'GET', id },
    { method: 'GET', id, range: { start: 4000, end: null } }
  ]);
  assert.equal(past.status, 416);
  assert.equal(past.headers['content-range'], 'bytes */3000');
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

test('driver interruptKeys, when present, is a non-empty list of keys', () => {
  const valid = {
    name: 'Example', stateDir: '.example', authFilename: 'auth.json',
    attachmentCapabilities: { nativeImages: false, localImagePaths: true, mcpImages: true },
    buildCommand() {}, isResumeCommand() {}, isMissingResume() {}, prepare() {}
  };
  assert.doesNotThrow(() => validateDriver({ ...valid, interruptKeys: ['\x1b', '\x1b'] }));
  for (const interruptKeys of ['\x1b', [], [''], ['\x1b', 27]]) {
    assert.throws(() => validateDriver({ ...valid, interruptKeys }), /interruptKeys must be a non-empty array of strings/);
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

// #261: "Send now" for a message in the CLI's own queue interrupts with the driver's key, which
// makes the pinned CLIs submit their queue; Ctrl+C would not.
test('an interrupt request writes the driver interruption keys, a pause apart', async () => {
  const delays = [];
  const harness = createHarness({}, { interruptKeys: ['\x1b', '\x1b'] }, {
    setTimeout(callback, ms) { delays.push(ms); callback(); return 1; }
  });
  const socket = new FakeSocket();
  harness.runtime.webSocketServer.connect(socket, '/');
  socket.emit('message', Buffer.from(JSON.stringify({ type: 'interrupt' })));
  await new Promise(resolve => setImmediate(resolve));
  assert.deepEqual(harness.terminals[0].writes, ['\x1b', '\x1b']);
  assert.ok(delays.includes(300));

  const single = createHarness();
  const other = new FakeSocket();
  single.runtime.webSocketServer.connect(other, '/');
  other.emit('message', Buffer.from(JSON.stringify({ type: 'interrupt' })));
  await new Promise(resolve => setImmediate(resolve));
  assert.deepEqual(single.terminals[0].writes, ['\x1b']);
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

  // Export waits for xterm's write callback before the final persistence run.
  await waitForExport(() => harness.commands.length >= 4);

  assert.deepEqual(harness.writes, [{ file: '/tmp/agenthub-scrollback-proof/scrollback.log', data: 'saved output' }]);
  assert.equal(harness.commands.length, 4);
  // Every presigned url goes to curl as an argument, never through a shell.
  assert.deepEqual(harness.commands[0], { file: 'curl',
    args: ['-fsS', '-k', '--max-time', '120', '-T', '/tmp/agenthub-scrollback-proof/scrollback.log', 'https://storage.invalid/scrollback'] });
  assert.match(harness.commands[1].args[1], /-path "\.test-agent\/auth\.json" -o /);
  assert.match(harness.commands[2].args[1], /tar -I 'gzip -1' -cf "\$1\.part" /);
  assert.deepEqual(harness.commands[2].args.slice(2), ['sh', '/tmp/agenthub-state-proof/state.tgz']);
  // The home directory is the working directory, not an argument a shell would see.
  assert.deepEqual(harness.commands[2].options, { cwd: '/home/agent' });
  assert.deepEqual(harness.commands[1].options, { cwd: '/home/agent' });
  assert.doesNotMatch(harness.commands[2].args[1], /curl|storage\.invalid/);
  assert.deepEqual(harness.commands[3], { file: 'curl',
    args: ['-fsS', '-k', '--max-time', '120', '-T', '/tmp/agenthub-state-proof/state.tgz', 'https://storage.invalid/state'] });
  assert.deepEqual(harness.commands[1].args.slice(2), []);
  assert.match(harness.commands[2].args[1], /"\.test-agent"/);
  assert.match(harness.commands[2].args[1], /--exclude="\.test-agent\/auth\.json"/);
  assert.doesNotMatch(harness.commands[2].args[1], /cat |credentials\.json/);
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
  assert.equal(harness.commands.length, 3);
  const command = harness.commands[1].args[1];
  assert.match(command, /--exclude="\.openclaw\/auth-profiles\.json"/);
  assert.match(command, /--exclude="\.openclaw\/agents\/main\/agent\/auth-profiles\.json"/);
  assert.match(command, /--exclude="\.openclaw\/agents\/\*\/agent\/openclaw-agent\.sqlite"/);
  // The change signature has to leave out the same paths, or a credential refresh alone
  // would count as changed state and trigger an archive that does not even contain it.
  const signature = harness.commands[0].args[1];
  assert.match(signature, /-path "\.openclaw\/auth-profiles\.json" -o /);
  assert.match(signature, /-path "\.openclaw\/agents\/\*\/agent\/openclaw-agent\.sqlite" -o /);
});

test('common transport keeps the last archive unless tar finished, accepting files changed while read', () => {
  const harness = createHarness({ AGENTHUB_STATE_PUT_URL: 'https://storage.invalid/state' });
  harness.intervals[0].callback();
  const command = harness.commands[1].args[1];
  assert.match(command, /^nice -n 10 timeout 120 tar -I 'gzip -1' -cf "\$1\.part" /);
  assert.match(command, /if \[ \$\? -le 1 \]; then mv -f "\$1\.part" "\$1"; /);
  assert.match(command, /else rm -f "\$1\.part"; exit 1; fi$/);
});

test('common transport skips a persistence tick while the previous run is still archiving', () => {
  const harness = createHarness({ AGENTHUB_STATE_PUT_URL: 'https://storage.invalid/state' },
    {}, { deferExec: true });
  harness.intervals[0].callback();
  harness.intervals[0].callback();
  harness.intervals[0].callback();
  assert.equal(harness.commands.length, 1);

  harness.pendingExec.shift()(null, SIGNATURE_A);
  harness.intervals[0].callback();
  harness.intervals[0].callback();
  assert.equal(archives(harness).length, 1);
  assert.equal(harness.commands.length, 2);

  // The archive is written; its upload is the next command, and the run is still in flight.
  harness.pendingExec.shift()();
  harness.intervals[0].callback();
  assert.equal(harness.commands.length, 3);
  assert.equal(harness.commands[2].file, 'curl');

  harness.pendingExec.shift()();
  harness.intervals[0].callback();
  assert.equal(harness.commands.length, 3, 'the tick after an upload is inside the interval');
});

test('common transport runs one final persistence after an in-flight run on SIGTERM', () => {
  const harness = createHarness({ AGENTHUB_STATE_PUT_URL: 'https://storage.invalid/state' },
    {}, { deferExec: true });
  harness.intervals[0].callback();
  harness.signals.SIGTERM();
  harness.signals.SIGTERM();
  assert.equal(harness.commands.length, 1);
  assert.deepEqual(harness.exits, []);

  harness.pendingExec.shift()(null, SIGNATURE_A);
  harness.pendingExec.shift()();
  harness.pendingExec.shift()();
  assert.equal(archives(harness).length, 2, 'the state written since the tick is archived once more');
  assert.deepEqual(harness.exits, []);

  harness.pendingExec.shift()();
  harness.pendingExec.shift()();
  assert.deepEqual(harness.exits, [0, 0]);
  assert.equal(harness.commands.length, 5, 'the final run does not spend time on a signature');
});

// What `sha256sum` prints for two different listings of the state directory.
const SIGNATURE_A = 'a'.repeat(64) + '  -\n';
const SIGNATURE_B = 'b'.repeat(64) + '  -\n';

function archives(harness) {
  return harness.commands.filter(command => /tar -I 'gzip -1' -cf/.test(command.args[1]));
}

function signatures(harness) {
  return harness.commands.filter(command => /sha256sum$/.test(command.args[1]));
}

// A harness whose state directory reports `state.signature` and whose upload fails while
// `state.uploadFails` is set.
function archiveHarness(state) {
  return createHarness({ AGENTHUB_STATE_PUT_URL: 'https://storage.invalid/state' }, {}, {
    execResult: command => /sha256sum$/.test(command)
      ? { stdout: state.signature }
      : { error: state.uploadFails ? new Error('curl: (22) 503') : null }
  });
}

test('common transport archives the state on the first tick and not again while it is unchanged', () => {
  const state = { signature: SIGNATURE_A };
  const harness = archiveHarness(state);
  for (let tick = 0; tick < 20; tick++) harness.intervals[0].callback();

  assert.equal(archives(harness).length, 1, 'an idle session archives once');
  // After the interval the signature is asked on every tick, so a change is at most one tick
  // old when it is archived; inside the interval not even the listing runs.
  assert.equal(signatures(harness).length, 1 + (19 - 3));
});

test('common transport archives changed state, at most once per interval', () => {
  const state = { signature: SIGNATURE_A };
  const harness = archiveHarness(state);
  harness.intervals[0].callback();
  assert.equal(archives(harness).length, 1);

  state.signature = SIGNATURE_B;
  for (let tick = 0; tick < 3; tick++) harness.intervals[0].callback();
  assert.equal(archives(harness).length, 1, 'three ticks after an upload are inside the interval');
  assert.equal(signatures(harness).length, 1);

  harness.intervals[0].callback();
  assert.equal(archives(harness).length, 2);

  // A session that changes on every tick still archives only every fourth one.
  for (let tick = 0; tick < 8; tick++) {
    state.signature = String(tick).repeat(64) + '  -\n';
    harness.intervals[0].callback();
  }
  assert.equal(archives(harness).length, 4);
});

test('common transport retries a failed state upload on the next tick and does not remember it', () => {
  const state = { signature: SIGNATURE_A, uploadFails: true };
  const harness = archiveHarness(state);
  harness.intervals[0].callback();
  harness.intervals[0].callback();
  harness.intervals[0].callback();
  assert.equal(archives(harness).length, 3, 'unchanged state is archived again until an upload succeeds');

  state.uploadFails = false;
  harness.intervals[0].callback();
  assert.equal(archives(harness).length, 4);
  for (let tick = 0; tick < 8; tick++) harness.intervals[0].callback();
  assert.equal(archives(harness).length, 4, 'only the successful upload counts as done');
});

test('common transport archives when the change signature cannot be taken', () => {
  for (const stdout of ['', undefined, 'sha256sum: not found\n']) {
    const harness = createHarness({ AGENTHUB_STATE_PUT_URL: 'https://storage.invalid/state' }, {}, {
      execResult: command => /sha256sum$/.test(command) ? { stdout } : {}
    });
    for (let tick = 0; tick < 9; tick++) harness.intervals[0].callback();
    assert.equal(archives(harness).length, 3, 'every interval, as before change detection existed');
  }
  const failing = createHarness({ AGENTHUB_STATE_PUT_URL: 'https://storage.invalid/state' }, {}, {
    execResult: command => /sha256sum$/.test(command)
      ? { error: new Error('timeout'), stdout: SIGNATURE_A } : {}
  });
  for (let tick = 0; tick < 9; tick++) failing.intervals[0].callback();
  assert.equal(archives(failing).length, 3);
});

test('common transport always archives on SIGTERM, unchanged state and interval notwithstanding', () => {
  const state = { signature: SIGNATURE_A };
  const harness = archiveHarness(state);
  harness.intervals[0].callback();
  harness.intervals[0].callback();
  assert.equal(archives(harness).length, 1);

  harness.signals.SIGTERM();
  assert.equal(archives(harness).length, 2);
  assert.equal(signatures(harness).length, 1, 'the final run does not consult the signature');
  assert.deepEqual(harness.exits, [0]);
});

test('common transport always archives when the agent exits, unchanged state and interval notwithstanding', async () => {
  const state = { signature: SIGNATURE_A };
  const harness = archiveHarness(state);
  harness.intervals[0].callback();
  harness.intervals[0].callback();
  assert.equal(archives(harness).length, 1);

  harness.terminals[0].emitExit({ exitCode: 0 });
  // Export waits for xterm's write callback before the final persistence run.
  await waitForExport(() => archives(harness).length >= 2);

  assert.equal(archives(harness).length, 2);
  assert.equal(signatures(harness).length, 1);
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

test('common transport flushes the credential watcher when the agent itself ends', async () => {
  const { harness, events } = watcherHarness(1);
  harness.terminals[0].emitExit({ exitCode: 0, signal: 0 });

  // Export waits for xterm's write callback before the final persistence run.
  await waitForExport(() => harness.exits.length > 0);

  assert.deepEqual(events, [[4242, 'SIGTERM'], [4242, 0]]);
  assert.deepEqual(harness.exits, [0]);
});

// #244: an autonomous Claude run that failed on its login was listed as "agent start: Error",
// because nothing wrote a termination message and Kubernetes had only the container's reason.
// The message is written after the sign-in filter has caught up, so wait for the exit itself.
async function settle(harness) {
  for (let attempt = 0; attempt < 200 && harness.exits.length === 0; attempt++)
    await new Promise(resolve => setTimeout(resolve, 5));
}

test('a failed agent exit leaves the CLI\'s last words as the termination message', async () => {
  const harness = createHarness({
    AGENTHUB_MODE: 'autonomous',
    TERMINATION_MESSAGE_PATH: '/run/termination'
  });
  harness.terminals[0].emitData('\x1b[?25lStarting\r\n\x1b[31mFailed to authenticate: ' +
    'OAuth session expired and could not be refreshed\x1b[0m\r\n\x1b[?25h');
  harness.terminals[0].emitExit({ exitCode: 1, signal: 0 });
  await settle(harness);

  const written = harness.writes.filter(write => write.file === '/run/termination');
  assert.deepEqual(written.map(write => write.data), [
    'Test exited with code 1: Starting | Failed to authenticate: OAuth session expired and could not be refreshed'
  ]);
  assert.deepEqual(harness.exits, [1]);
});

// The termination message is shown to everyone who can read the session list, so it carries
// nothing the scrollback upload would not: a session secret value, a sign-in URL with its
// authorization parameters and a device code are all gone before it is written.
test('a failed agent exit masks secrets, sign-in URLs and device codes in its termination message', async () => {
  const secret = 'synthetic-session-secret-4f1c9a';
  const harness = createHarness({
    AGENTHUB_MODE: 'autonomous',
    TERMINATION_MESSAGE_PATH: '/run/termination',
    AGENTHUB_SECRET_ENV: 'DEPLOY_KEY',
    DEPLOY_KEY: secret
  });
  harness.terminals[0].emitData('deploy key ' + secret + ' rejected\r\n' +
    'Open https://auth.example.com/oauth/authorize?client_id=c1&state=s7a8b9c0d1&code_challenge=ch4ll3ng3 to sign in\r\n' +
    'Enter code WXYZ-4821 and token=abc123def456\r\n' +
    'Error: login required\r\n');
  harness.terminals[0].emitExit({ exitCode: 1, signal: 0 });
  await settle(harness);

  const [written] = harness.writes.filter(write => write.file === '/run/termination').map(write => write.data);
  assert.ok(written, 'no termination message');
  for (const leaked of [secret, 's7a8b9c0d1', 'ch4ll3ng3', 'client_id', 'WXYZ-4821', 'abc123def456'])
    assert.ok(!written.includes(leaked), leaked + ' leaked: ' + written);
  assert.match(written, /^Test exited with code 1: /);
  assert.match(written, /Error: login required$/);
});

test('a successful agent exit writes no termination message', async () => {
  const harness = createHarness({ TERMINATION_MESSAGE_PATH: '/run/termination' });
  harness.terminals[0].emitData('done\r\n');
  harness.terminals[0].emitExit({ exitCode: 0, signal: 0 });
  await settle(harness);

  assert.equal(harness.writes.some(write => write.file === '/run/termination'), false);
  assert.deepEqual(harness.exits, [0]);
});

test('exitReason bounds and masks what it quotes', () => {
  const { exitReason } = require('../../common/server');
  assert.equal(exitReason('Codex', 2, 'SIGKILL', ''), 'Codex exited with code 2, signal SIGKILL without output.');
  const reason = exitReason('Codex', 1, undefined, 'x'.repeat(10_000) + '\nkey sk-synthetic\n',
    text => text.replaceAll('sk-synthetic', '****'));
  assert.ok(reason.length < 700, reason.length);
  assert.match(reason, /key \*\*\*\*$/);
});

// #287: real Codex drew its device code and exited 1 in the same breath when the device token
// request failed, and main put the code into the termination message. The exit arrives while the
// sign-in filter still has the frame queued; what is written must hold neither the code nor the
// URL, and must still say why the login failed.
test('a device-code frame followed at once by a failed exit keeps the code out of the termination message', async () => {
  const harness = createHarness({ TERMINATION_MESSAGE_PATH: '/run/termination' });
  harness.terminals[0].emitData('Welcome to Codex\r\n\r\nFollow these steps to sign in with ChatGPT using device code authorization:\r\n\r\n' +
    '1. Open this link in your browser and sign in to your account\r\n   https://auth.example.com/codex/device\r\n\r\n' +
    '2. Enter this one-time code (expires in 15 minutes)\r\n   WXYZ-48210\r\n\r\n');
  harness.terminals[0].emitData('Error logging in with device code: device auth failed with status 500 Internal Server Error\r\n');
  harness.terminals[0].emitExit({ exitCode: 1, signal: 0 });
  await settle(harness);

  const [written] = harness.writes.filter(write => write.file === '/run/termination').map(write => write.data);
  assert.ok(written, 'no termination message');
  assert.ok(!written.includes('WXYZ-48210'), written);
  assert.match(written, /device auth failed with status 500/);
});

test('exitReason removes device and paste codes by shape', () => {
  const { exitReason } = require('../../common/server');
  const reason = exitReason('Test', 1, undefined,
    'code ABCD-EFGH\ncode QWERTYUI\npaste aB3_dE5-fG7hI9jK1lM3nO5pQ7rS9\nError: login failed\n');
  for (const leaked of ['ABCD-EFGH', 'QWERTYUI', 'aB3_dE5-fG7hI9jK1lM3nO5pQ7rS9'])
    assert.ok(!reason.includes(leaked), leaked + ' leaked: ' + reason);
  assert.match(reason, /Error: login failed$/);
});

test('exitReason drops environment dumps and scrubs credentials by shape', () => {
  const { exitReason } = require('../../common/server');
  const reason = exitReason('Codex', 1, undefined,
    'AGENTHUB_CALLBACK_TOKEN=synthetic-token-value\nexport OPENAI_API_KEY=sk-synthetic12345678\n' +
    'fetch https://user:pw@git.example.com/repo failed: Authorization: Bearer eyJhbGciOiJIUzI1NiJ9.e30.sig\n');
  for (const leaked of ['synthetic-token-value', 'sk-synthetic12345678', 'pw@', 'eyJhbGciOiJIUzI1NiJ9'])
    assert.ok(!reason.includes(leaked), leaked + ' leaked: ' + reason);
  assert.match(reason, /git\.example\.com\/repo failed/);
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

  await waitForExport(() => harness.requests.length >= 4);
  assert.deepEqual(harness.requests.map(request => [request.url, request.options.method]), [
    ['https://backend.invalid/internal/session/status', 'POST'],
    // A fresh session agent clears a question dialog its predecessor may have left open (#361).
    ['https://backend.invalid/internal/session/question-state', 'POST'],
    ['https://backend.invalid/internal/session/scrollback', 'PUT'],
    ['https://backend.invalid/internal/session/status', 'POST']
  ]);
  assert.equal(harness.requests[0].options.body, JSON.stringify({ status: 'Running' }));
  const newline = String.fromCharCode(13, 10);
  assert.equal(harness.requests[1].options.body, JSON.stringify({ open: false }));
  assert.equal(harness.requests[2].options.body,
    'completed output' + newline + '[agent] Session ended (code 0).' + newline);
  assert.equal(harness.requests[3].options.body, JSON.stringify({ status: 'Succeeded' }));
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

test('Codex device-auth resume retries fresh exactly once without repeating login', t => {
  const home = fs.mkdtempSync(path.join(require('node:os').tmpdir(), 'codex-resume-test-'));
  t.after(() => fs.rmSync(home, { recursive: true, force: true }));
  const codexDriver = require('../../codex/driver');
  const harness = createHarness({
    AGENTHUB_CODEX_DEVICE_AUTH: '1',
    AGENTHUB_RESUME: '1',
    AGENTHUB_STATE_RESTORED: '1',
    CODEX_HOME: home
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

test('common transport does not infer resume merely because the first launch allows it', async () => {
  let missingResumeChecks = 0;
  const harness = createHarness({}, {
    isResumeCommand: () => false,
    isMissingResume: () => {
      missingResumeChecks++;
      return true;
    }
  });

  harness.terminals[0].emitExit({ exitCode: 1, signal: 0 });

  // Export waits for xterm's write callback before the final persistence run.
  await waitForExport(() => harness.exits.length > 0);

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

  // Export waits for xterm's write callback before the final persistence run.
  await waitForExport(() => harness.requests.at(-1)?.options.body === JSON.stringify({ status: 'Failed' }));

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
  assert.equal(harness.commands.length, 3);
  assert.match(harness.commands[1].args[1], /"\.codex"/);
  assert.match(harness.commands[1].args[1], /--exclude="\.codex\/auth\.json"/);
});

test('common transport uploads the native transcript next to the scrollback once the driver finds it', async () => {
  const file = '/home/agent/.claude/projects/-workspace-repo/fixed.jsonl';
  const files = {};
  let visible = false;
  const harness = createHarness({
    AGENTHUB_CALLBACK_URL: 'https://backend.invalid/internal/session',
    AGENTHUB_CALLBACK_TOKEN: 'synthetic-callback-token',
    AGENTHUB_TRANSCRIPT_PUT_URL: 'https://storage.invalid/transcript',
    AGENTHUB_S3_INSECURE: '1'
  }, {
    findTranscript: context => {
      assert.equal(context.home, '/home/agent');
      assert.equal(context.cwd, '/workspace/repo');
      assert.equal(typeof context.launchedAt, 'number');
      return visible ? file : null;
    }
  }, { files });

  // Nothing exists yet: no upload, and the driver is asked again next time.
  harness.intervals[0].callback();
  await tick();
  assert.equal(harness.commands.length, 0);
  assert.equal(harness.requests.filter(r => r.url.endsWith('/transcript')).length, 0);

  visible = true;
  files[file] = '{"type":"user"}\n{"type":"assistant"}\n';
  harness.intervals[0].callback();
  await tick();
  const s3 = harness.commands.find(c => c.file === 'curl' && c.args.includes('-T'));
  assert.ok(s3, 'the whole file goes to S3');
  // Argument list, not a shell string: the CLI picks the file name, so it must never be parsed.
  assert.deepEqual(s3.args, ['-fsS', '-k', '--max-time', '120', '-T', file, 'https://storage.invalid/transcript']);
  const hub = harness.requests.find(r => r.url.endsWith('/transcript'));
  assert.ok(hub, 'and the hub gets a copy');
  assert.equal(hub.options.method, 'PUT');
  assert.equal(hub.options.headers['Content-Type'], 'application/x-ndjson');
  assert.equal(hub.options.body, files[file]);

  // Unchanged file: nothing is re-uploaded on the next tick.
  harness.intervals[0].callback();
  await tick();
  assert.equal(harness.requests.filter(r => r.url.endsWith('/transcript')).length, 1);
  assert.equal(harness.commands.filter(c => c.file === 'curl' && c.args.includes('-T')).length, 1);
});

test('common transport sends the hub the capped tail of the transcript cut at a line boundary', async () => {
  const file = '/home/agent/.codex/sessions/2026/10/10/rollout-x-' + 'a'.repeat(8) + '.jsonl';
  const line = '{"n":' + '1'.repeat(MAX_BUFFER / 2) + '}\n';
  const files = { [file]: line + line + '{"last":true}\n' };
  const harness = createHarness({
    AGENTHUB_CALLBACK_URL: 'https://backend.invalid/internal/session',
    AGENTHUB_CALLBACK_TOKEN: 'synthetic-callback-token'
  }, { findTranscript: () => file }, { files });

  harness.intervals[0].callback();
  await tick();
  const hub = harness.requests.find(r => r.url.endsWith('/transcript'));
  assert.ok(hub.options.body.length <= MAX_BUFFER);
  assert.ok(hub.options.body.startsWith('{"'), 'the first kept line is whole');
  assert.ok(hub.options.body.endsWith('{"last":true}\n'));
});

test('common transport ignores a transcript path a driver could not have derived', async () => {
  const hostile = '/home/agent/x"; rm -rf /; echo ".jsonl';
  const harness = createHarness({
    AGENTHUB_CALLBACK_URL: 'https://backend.invalid/internal/session',
    AGENTHUB_CALLBACK_TOKEN: 'synthetic-callback-token'
  }, { findTranscript: () => hostile }, { files: { [hostile]: '{}' } });

  harness.intervals[0].callback();
  await tick();
  assert.equal(harness.requests.filter(r => r.url.endsWith('/transcript')).length, 0);
});

test('common transport validates findTranscript when a driver declares one', () => {
  const base = {
    name: 'Example', stateDir: '.example', authFilename: 'auth.json',
    attachmentCapabilities: { nativeImages: false, localImagePaths: true, mcpImages: true },
    buildCommand() {}, isResumeCommand() {}, isMissingResume() {}, prepare() {}
  };
  assert.throws(() => validateDriver({ ...base, findTranscript: '/not/a/function' }),
    /findTranscript must be a function/);
  assert.equal(typeof validateDriver({ ...base, findTranscript: () => null }).findTranscript, 'function');
});

// ---- PUT /agenthub/credentials: another account for a running session ----------------------

const credentialHeaders = { 'X-Agent-Token': 'correct-token', 'X-Agent-Provider': 'test', 'Content-Length': '7' };

test('credential route rejects a bad token, another provider, a wrong method and a malformed body', async () => {
  const harness = createHarness({ AGENTHUB_CALLBACK_TOKEN: 'correct-token' });

  const missing = await requestHttp(harness, 'PUT', '/agenthub/credentials', { 'X-Agent-Provider': 'test' }, '{"a":1}');
  const wrongToken = await requestHttp(harness, 'PUT', '/agenthub/credentials',
    { ...credentialHeaders, 'X-Agent-Token': 'wrong' }, '{"a":1}');
  const otherProvider = await requestHttp(harness, 'PUT', '/agenthub/credentials',
    { ...credentialHeaders, 'X-Agent-Provider': 'other' }, '{"a":1}');
  const get = await requestHttp(harness, 'GET', '/agenthub/credentials', credentialHeaders);
  const junk = await requestHttp(harness, 'PUT', '/agenthub/credentials', credentialHeaders, 'nope');
  const array = await requestHttp(harness, 'PUT', '/agenthub/credentials', credentialHeaders, '[1,2,3]');
  const oversized = await requestHttp(harness, 'PUT', '/agenthub/credentials',
    { ...credentialHeaders, 'Content-Length': String(70 * 1024) }, '{}');

  assert.deepEqual([missing.status, wrongToken.status, otherProvider.status, get.status, junk.status, array.status, oversized.status],
    [401, 401, 409, 405, 400, 400, 413]);
  assert.equal(harness.writes.length, 0);
  assert.equal(harness.terminals[0].killed, false);
});

test('credential route writes the watcher baseline, installs the file under HOME and restarts with resume', async () => {
  const harness = createHarness({
    AGENTHUB_CALLBACK_TOKEN: 'correct-token', AGENTHUB_CALLBACK_URL: 'http://hub.invalid/internal/sessions/s1',
    AGENTHUB_RESUME: '0', AGENTHUB_STATE_RESTORED: '0'
  });
  const socket = new FakeSocket();
  harness.runtime.webSocketServer.connect(socket, '/');
  socket.emit('message', JSON.stringify({ type: 'resize', cols: 200, rows: 50 }));
  const body = '{"a":1}';

  const response = await requestHttp(harness, 'PUT', '/agenthub/credentials', credentialHeaders, body);

  assert.equal(response.status, 202);
  assert.deepEqual(JSON.parse(response.body), { installed: true, restarting: true });
  // Baseline before file, so a watcher poll in between never sees an unknown hash.
  const expectedHash = require('node:crypto').createHash('sha256').update(body).digest('hex');
  assert.equal(harness.writes[0].file, path.join('/home/agent', '.agenthub', 'credential-baseline'));
  assert.equal(harness.writes[0].data, expectedHash + '\n');
  const target = path.resolve('/home/agent', '.test-agent', 'auth.json');
  assert.equal(harness.writes[1].data.toString(), body);
  assert.equal(harness.renames.length, 1);
  assert.equal(harness.renames[0].to, target);
  assert.equal(path.dirname(harness.renames[0].from), path.dirname(target));
  assert.equal(harness.terminals[0].killed, true);

  // The old process exits; the replacement resumes, at the size the client last asked for.
  harness.terminals[0].emitExit({ exitCode: 0, signal: 0 });
  assert.equal(harness.terminals.length, 2);
  assert.deepEqual(harness.spawns.map(spawn => spawn.args), [['resume'], ['resume']]);
  assert.equal(harness.spawns[1].options.cols, 200);
  assert.equal(harness.spawns[1].options.rows, 50);
  assert.equal(harness.runtime.env.AGENTHUB_RESUME, '1');
  assert.equal(harness.runtime.env.AGENTHUB_STATE_RESTORED, '1');
  assert.match(socket.sent.at(-1), /Provider account switched — restarting the agent and resuming the conversation/);
  assert.deepEqual(socket.closed, []);
  assert.deepEqual(harness.exits, []);
  // Only the initial "Running"; a restart is not an end of the session, so no terminal status.
  const statuses = harness.requests.filter(request => request.url.endsWith('/status'))
    .map(request => JSON.parse(request.options.body).status);
  assert.deepEqual(statuses, ['Running']);
});

test('a restart whose resume is not recognised falls back to a fresh start once, like a cross-pod resume', async () => {
  const harness = createHarness({ AGENTHUB_CALLBACK_TOKEN: 'correct-token' }, {
    isMissingResume: output => output.includes('missing')
  });
  await requestHttp(harness, 'PUT', '/agenthub/credentials', credentialHeaders, '{"a":1}');
  harness.terminals[0].emitExit({ exitCode: 0, signal: 0 });
  harness.terminals[1].emitData('missing state');
  harness.terminals[1].emitExit({ exitCode: 1, signal: 0 });

  assert.deepEqual(harness.spawns.map(spawn => spawn.args), [['resume'], ['resume'], ['fresh']]);
  assert.deepEqual(harness.exits, []);
});

test('credential route uses the driver validator and install hook when the driver has them', async () => {
  const installs = [];
  const harness = createHarness({ AGENTHUB_CALLBACK_TOKEN: 'correct-token', CUSTOM_AUTH: '/home/agent/.config/x/auth.json' }, {
    credentialPath: env => env.CUSTOM_AUTH,
    validCredential: buffer => buffer.toString().includes('"ok"'),
    installCredential: (env, body, target) => { installs.push({ body: body.toString(), target }); }
  });

  const rejected = await requestHttp(harness, 'PUT', '/agenthub/credentials', credentialHeaders, '{"a":1}');
  const accepted = await requestHttp(harness, 'PUT', '/agenthub/credentials', credentialHeaders, '{"ok":1}');

  assert.equal(rejected.status, 400);
  assert.equal(accepted.status, 202);
  assert.deepEqual(installs, [{ body: '{"ok":1}', target: path.resolve('/home/agent/.config/x/auth.json') }]);
  assert.equal(harness.renames.length, 0);
});

test('credential route refuses a path the driver points outside HOME', async () => {
  const harness = createHarness({ AGENTHUB_CALLBACK_TOKEN: 'correct-token' }, {
    credentialPath: () => '/etc/shadow'
  });

  const response = await requestHttp(harness, 'PUT', '/agenthub/credentials', credentialHeaders, '{"a":1}');

  assert.equal(response.status, 500);
  assert.equal(harness.writes.length, 0);
  assert.equal(harness.terminals[0].killed, false);
});

test('credential route answers 409 once the agent has ended for good', async () => {
  const harness = createHarness({ AGENTHUB_CALLBACK_TOKEN: 'correct-token' }, { isResumeCommand: () => false });
  harness.terminals[0].emitExit({ exitCode: 0, signal: 0 });

  const response = await requestHttp(harness, 'PUT', '/agenthub/credentials', credentialHeaders, '{"a":1}');

  assert.equal(response.status, 409);
  assert.equal(JSON.parse(response.body).error, 'agent_exited');
});

test('chat transport announces a credential restart as a durable info event and keeps the prompt unsent', async () => {
  const harness = createChatHarness({ AGENTHUB_CALLBACK_TOKEN: 'correct-token', AGENTHUB_PROMPT: 'do it' },
    { isResumeCommand: () => false });
  await tick();
  assert.equal(harness.children[0].stdinWrites.length, 1);

  const response = await requestHttp(harness, 'PUT', '/agenthub/credentials', credentialHeaders, '{"a":1}');
  assert.equal(response.status, 202);
  assert.equal(harness.children[0].killed, true);
  harness.children[0].emitExit(0, null);
  await tick();

  assert.equal(harness.children.length, 2);
  assert.equal(harness.children[1].stdinWrites.length, 0);
  const socket = new FakeSocket();
  harness.runtime.webSocketServer.connect(socket, '/');
  const events = socket.sent[0].trim().split('\n').map(line => JSON.parse(line));
  assert.ok(events.some(event => event.type === 'agenthub' && event.subtype === 'info' && /Provider account switched/.test(event.text)));
});

test('driver contract rejects credential hooks that are not functions', () => {
  const base = {
    name: 'Test', stateDir: '.t', authFilename: 'a.json',
    attachmentCapabilities: { nativeImages: false, localImagePaths: true, mcpImages: true },
    buildCommand() {}, isResumeCommand() {}, isMissingResume() {}, prepare() {}
  };
  assert.doesNotThrow(() => validateDriver({ ...base, credentialPath: () => null }));
  assert.throws(() => validateDriver({ ...base, credentialPath: '/x' }), /credentialPath/);
  assert.throws(() => validateDriver({ ...base, validCredential: true }), /validCredential/);
  assert.throws(() => validateDriver({ ...base, installCredential: {} }), /installCredential/);
});

// ---- PUT /agenthub/credentials/refresh: the same login with tokens the hub renewed -------------

test('refresh route installs the renewed file without restarting a driver that reloads it live', async () => {
  const harness = createHarness({ AGENTHUB_CALLBACK_TOKEN: 'correct-token' }, { reloadsCredentialLive: true });
  const body = '{"a":2}';

  const response = await requestHttp(harness, 'PUT', '/agenthub/credentials/refresh', credentialHeaders, body);

  assert.equal(response.status, 200);
  assert.deepEqual(JSON.parse(response.body), { installed: true, restarting: false });
  const expectedHash = require('node:crypto').createHash('sha256').update(body).digest('hex');
  assert.equal(harness.writes[0].file, path.join('/home/agent', '.agenthub', 'credential-baseline'));
  assert.equal(harness.writes[0].data, expectedHash + '\n');
  assert.equal(harness.writes[1].data.toString(), body);
  assert.equal(harness.renames[0].to, path.resolve('/home/agent', '.test-agent', 'auth.json'));
  assert.equal(harness.terminals[0].killed, false);
  assert.equal(harness.terminals.length, 1);
});

test('refresh route refuses a driver whose CLI is not known to re-read its file, and checks like the swap', async () => {
  const plain = createHarness({ AGENTHUB_CALLBACK_TOKEN: 'correct-token' });
  const unsupported = await requestHttp(plain, 'PUT', '/agenthub/credentials/refresh', credentialHeaders, '{"a":1}');
  assert.equal(unsupported.status, 409);
  assert.match(unsupported.body, /live_reload_unsupported/);
  assert.equal(plain.writes.length, 0);

  const live = createHarness({ AGENTHUB_CALLBACK_TOKEN: 'correct-token' }, { reloadsCredentialLive: true });
  const wrongToken = await requestHttp(live, 'PUT', '/agenthub/credentials/refresh',
    { ...credentialHeaders, 'X-Agent-Token': 'wrong' }, '{"a":1}');
  const otherProvider = await requestHttp(live, 'PUT', '/agenthub/credentials/refresh',
    { ...credentialHeaders, 'X-Agent-Provider': 'other' }, '{"a":1}');
  const junk = await requestHttp(live, 'PUT', '/agenthub/credentials/refresh', credentialHeaders, 'nope');
  assert.deepEqual([wrongToken.status, otherProvider.status, junk.status], [401, 409, 400]);
  assert.equal(live.writes.length, 0);
});

test('refresh route still installs after the agent exited, for whatever starts it next', async () => {
  const harness = createHarness({ AGENTHUB_CALLBACK_TOKEN: 'correct-token' }, { reloadsCredentialLive: true });
  harness.terminals[0].emitExit({ exitCode: 0, signal: 0 });

  const response = await requestHttp(harness, 'PUT', '/agenthub/credentials/refresh', credentialHeaders, '{"a":3}');

  assert.equal(response.status, 200);
  assert.equal(harness.renames.length, 1);
});

test('common transport validates reloadsCredentialLive when a driver declares it', () => {
  const base = {
    name: 'Example', stateDir: '.example', authFilename: 'auth.json',
    attachmentCapabilities: { nativeImages: false, localImagePaths: true, mcpImages: true },
    buildCommand() {}, isResumeCommand() {}, isMissingResume() {}, prepare() {}
  };
  assert.throws(() => validateDriver({ ...base, reloadsCredentialLive: 'yes' }), /reloadsCredentialLive must be a boolean/);
  assert.equal(validateDriver({ ...base, reloadsCredentialLive: true }).reloadsCredentialLive, true);
});

test('common transport validates turnHooks when a driver declares it', () => {
  const base = {
    name: 'Example', stateDir: '.example', authFilename: 'auth.json',
    attachmentCapabilities: { nativeImages: false, localImagePaths: true, mcpImages: true },
    buildCommand() {}, isResumeCommand() {}, isMissingResume() {}, prepare() {}
  };
  assert.throws(() => validateDriver({ ...base, turnHooks: 'yes' }), /turnHooks must be a boolean/);
  assert.equal(validateDriver({ ...base, turnHooks: true }).turnHooks, true);
});

test('state archives use separate real 0700 temporary directories and remove them after upload', t => {
  const root = fs.mkdtempSync(path.join(require('node:os').tmpdir(), 'archive-private-proof-'));
  t.after(() => fs.rmSync(root, { recursive: true, force: true }));
  const directories = [];
  const harness = createHarness({ TMPDIR: root, AGENTHUB_STATE_PUT_URL: 'https://example.com/state' }, {}, {
    mkdtempSync(prefix) {
      const directory = fs.mkdtempSync(prefix);
      assert.equal(fs.statSync(directory).mode & 0o777, 0o700);
      directories.push(directory); return directory;
    },
    rmSync: (file, options) => fs.rmSync(file, options)
  });
  harness.intervals[0].callback();
  harness.signals.SIGTERM();
  assert.equal(directories.length, 2);
  assert.notEqual(directories[0], directories[1]);
  for (const directory of directories) {
    assert.equal(fs.existsSync(directory), false);
    assert.ok(harness.commands.some(command => command.args.includes(path.join(directory, 'state.tgz'))));
  }
});
