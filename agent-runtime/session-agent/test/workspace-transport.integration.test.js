'use strict';

const assert = require('node:assert/strict');
const { once, EventEmitter } = require('node:events');
const fs = require('node:fs');
const http = require('node:http');
const os = require('node:os');
const path = require('node:path');
const { spawn } = require('node:child_process');
const test = require('node:test');
const { WebSocket, WebSocketServer } = require('ws');
const { createCommonServer } = require('../../common/server');

// A real child, TCP WebSocket, and HTTP callback exercise the production transport.
// The provider is deliberately a fixture: this test needs no subscription or model call.
test('workspace chat sends, receives, interrupts, replays, and uploads native history over real connections', { timeout: 15_000 }, async t => {
  const directory = fs.mkdtempSync(path.join(os.tmpdir(), 'agenthub-workspace-'));
  const transcriptPath = path.join(directory, 'conversation.jsonl');
  const requests = [];
  const callbacks = new EventEmitter();
  const intervals = [];
  const hub = http.createServer(async (request, response) => {
    let body = '';
    for await (const chunk of request) body += chunk;
    const received = { url: request.url, token: request.headers['x-agent-token'], body };
    requests.push(received);
    response.writeHead(204).end();
    callbacks.emit(request.url, received);
  });
  hub.listen(0, '127.0.0.1');
  await once(hub, 'listening');
  let child;
  let runtime;
  const sockets = [];
  t.after(async () => {
    for (const socket of sockets) socket.terminate();
    runtime?.webSocketServer.close();
    runtime?.httpServer.closeAllConnections();
    await new Promise(resolve => runtime ? runtime.httpServer.close(resolve) : resolve());
    if (child && child.exitCode === null) {
      child.removeAllListeners('exit');
      const ended = once(child, 'exit');
      child.kill();
      await ended;
    }
    hub.closeAllConnections();
    await new Promise(resolve => hub.close(resolve));
    fs.rmSync(directory, { recursive: true, force: true });
  });
  const provider = `
    const fs = require('node:fs');
    require('node:readline').createInterface({ input: process.stdin }).on('line', line => {
      const input = JSON.parse(line);
      const event = input.type === 'user'
        ? { type: 'assistant', message: { id: 'reply', content: [{ type: 'text', text: 'Reply: ' + input.message.content[0].text }] } }
        : { type: 'result', is_error: false, result: 'Interrupted' };
      const output = JSON.stringify(event) + String.fromCharCode(10);
      fs.appendFileSync(process.env.FIXTURE_TRANSCRIPT, output);
      process.stdout.write(output);
    });
  `;
  runtime = createCommonServer({
    env: {
      ...process.env, HOME: directory, AGENTHUB_WORKDIR: directory, AGENTHUB_PORT: '0',
      AGENTHUB_MODE: 'interactive', AGENTHUB_RESUME: '0', AGENTHUB_PROMPT: '',
      AGENTHUB_CALLBACK_URL: `http://127.0.0.1:${hub.address().port}`, AGENTHUB_CALLBACK_TOKEN: 'fixture-session-token',
      AGENTHUB_FILE_ROOT: path.join(directory, 'files'), FIXTURE_TRANSCRIPT: transcriptPath,
      AGENTHUB_STATE_PUT_URL: '', AGENTHUB_SCROLLBACK_PUT_URL: '', AGENTHUB_TRANSCRIPT_PUT_URL: ''
    },
    driver: {
      name: 'Fixture', stateDir: '.fixture', authFilename: 'auth.json',
      attachmentCapabilities: { nativeImages: false, localImagePaths: false, mcpImages: false },
      prepare() {}, buildCommand: () => ({ cmd: process.execPath, args: ['-e', provider], pipe: true }),
      isResumeCommand: () => false, isMissingResume: () => false,
      findTranscript: () => fs.existsSync(transcriptPath) ? transcriptPath : null
    },
    dependencies: {
      pty: {}, WebSocketServer,
      spawn: (command, args, options) => {
        // Skill syncing is outside this transport smoke test.
        if (args[0] !== '-e') {
          const skipped = new EventEmitter();
          skipped.kill = () => {};
          queueMicrotask(() => skipped.emit('exit', 0));
          return skipped;
        }
        child = spawn(command, args, options);
        return child;
      },
      process: Object.assign(new EventEmitter(), { exit() {} }),
      setInterval: callback => { intervals.push(callback); return 0; }
    }
  });
  if (!runtime.httpServer.listening) await once(runtime.httpServer, 'listening');
  const connect = async () => {
    const socket = new WebSocket(`ws://127.0.0.1:${runtime.httpServer.address().port}/`, 'tty');
    sockets.push(socket);
    const frames = [];
    const arrived = new EventEmitter();
    socket.on('message', bytes => {
      for (const line of bytes.toString().trim().split('\n')) {
        const value = JSON.parse(line);
        frames.push(value);
        arrived.emit('frame', value);
      }
    });
    await once(socket, 'open');
    const waitFor = predicate => {
      const saved = frames.find(predicate);
      if (saved) return Promise.resolve(saved);
      return new Promise(resolve => {
        const receive = frame => {
          if (!predicate(frame)) return;
          arrived.off('frame', receive);
          resolve(frame);
        };
        arrived.on('frame', receive);
      });
    };
    return { socket, frames, waitFor };
  };
  const first = await connect();
  t.diagnostic('runtime socket connected');
  first.socket.send(JSON.stringify({ type: 'chat', text: 'Build this', clientTurnId: 'turn-1' }));
  await first.waitFor(frame => frame.type === 'agenthub' && frame.subtype === 'chat_delivered');
  t.diagnostic('turn delivered');
  const reply = await first.waitFor(frame => frame.type === 'assistant');
  assert.equal(reply.message.content[0].text, 'Reply: Build this');
  t.diagnostic('provider reply received');
  first.socket.send(JSON.stringify({ type: 'interrupt' }));
  await first.waitFor(frame => frame.type === 'result' && frame.result === 'Interrupted');
  t.diagnostic('interrupt received');

  const second = await connect();
  t.diagnostic('second connection opened');
  assert.equal((await second.waitFor(frame => frame.type === 'assistant')).message.content[0].text, 'Reply: Build this');
  assert.equal(second.frames.filter(frame => frame.type === 'user').length, 1);

  const uploaded = once(callbacks, '/transcript');
  intervals[0]();
  const [native] = await uploaded;
  t.diagnostic('native history uploaded');
  assert.equal(native.token, 'fixture-session-token');
  assert.match(native.body, /Reply: Build this/);
  assert.match(native.body, /Interrupted/);
  assert.equal(requests.some(request => request.url.includes('fixture-session-token')), false);
});
