'use strict';

// Run against a runtime copy so the real transport cannot touch this session's watcher or archive.
const fs = require('node:fs');
const path = require('node:path');
const os = require('node:os');
const http = require('node:http');
const assert = require('node:assert/strict');
const { spawn } = require('node:child_process');
const { once } = require('node:events');
const WebSocket = require('ws');

const fileSecret = 'proof-file-value-245';
const envSecret = 'proof-env-value-245';
const token = 'proof-callback-245';
const sleep = ms => new Promise(resolve => setTimeout(resolve, ms));

if (process.argv[2] === 'worker') {
  const root = process.argv[3];
  const driver = {
    name: 'Proof', stateDir: '.proof', authFilename: 'auth.json',
    attachmentCapabilities: { nativeImages: false, localImagePaths: true, mcpImages: true },
    prepare() {}, isResumeCommand: () => false, isMissingResume: () => false,
    findTranscript: () => path.join(root, 'native.jsonl'),
    buildCommand: () => ({ cmd: process.execPath, args: ['-e',
      'console.log(require("node:fs").readFileSync(process.env.AGENTHUB_SECRETS_DIR + "/FILE_KEY", "utf8") + " " + process.env.ENV_KEY); setInterval(() => {}, 1000)'] })
  };
  require(path.join(root, 'runtime/common/server.js')).createCommonServer({ driver });
} else {
  main().catch(error => { console.error(error); process.exitCode = 1; });
}

async function main() {
  const root = fs.mkdtempSync(path.join(os.tmpdir(), 'mask-245-'));
  const runtime = path.join(root, 'runtime');
  fs.cpSync(path.resolve(__dirname, '../../agent-runtime'), runtime, {
    recursive: true, filter: source => !source.includes('node_modules')
  });
  const serverFile = path.join(runtime, 'common/server.js');
  fs.writeFileSync(serverFile, fs.readFileSync(process.env.PROOF_SERVER_SOURCE || serverFile, 'utf8')
    .replaceAll('/tmp/state.tgz', path.join(root, 'state.tgz'))
    .replaceAll('/tmp/scrollback.log', path.join(root, 'scrollback.log'))
    .replaceAll('/tmp/agenthub-auth-watcher.pid', path.join(root, 'watcher.pid')));
  fs.mkdirSync(path.join(root, '.proof'));
  fs.mkdirSync(path.join(root, 'secrets'));
  fs.writeFileSync(path.join(root, 'secrets/FILE_KEY'), fileSecret);
  const line = text => JSON.stringify({ type: 'assistant', message: { role: 'assistant', content: text } }) + '\n';
  const raw = line(fileSecret + ' ' + envSecret + ' größer');
  fs.writeFileSync(path.join(root, 'native.jsonl'), raw);
  const uploaded = new Map();
  const hub = http.createServer(async (req, res) => {
    const chunks = [];
    for await (const chunk of req) chunks.push(chunk);
    if (req.method === 'PUT') uploaded.set(req.url, Buffer.concat(chunks).toString());
    if (req.url.includes('/secrets/session')) {
      const env = req.url.includes('/sessionenv-');
      res.setHeader('Content-Type', 'application/json');
      res.end(JSON.stringify({ apiVersion: 'v1', kind: 'Secret', metadata: { name: env ? 'sessionenv-proof245' : 'sessionsecrets-proof245' },
        data: { [env ? 'ENV_KEY' : 'FILE_KEY']: Buffer.from(env ? envSecret : fileSecret).toString('base64') } }));
      return;
    }
    res.end(req.url === '/captured' ? JSON.stringify(Object.fromEntries(uploaded)) : '{}');
  });
  hub.listen(0, '127.0.0.1');
  await once(hub, 'listening');
  const callback = 'http://127.0.0.1:' + hub.address().port;
  const reservation = http.createServer();
  reservation.listen(0, '127.0.0.1');
  await once(reservation, 'listening');
  const port = reservation.address().port;
  await new Promise(resolve => reservation.close(resolve));
  const child = spawn(process.execPath, [__filename, 'worker', root], {
    env: { PATH: process.env.PATH, NODE_PATH: process.env.NODE_PATH, HOME: root,
      AGENTHUB_PORT: String(port), AGENTHUB_WORKDIR: root, AGENTHUB_SESSION_ID: 'proof245',
      AGENTHUB_CALLBACK_URL: callback, AGENTHUB_CALLBACK_TOKEN: token,
      AGENTHUB_SKILLS_SYNC_SECONDS: '0', AGENTHUB_SECRETS_DIR: path.join(root, 'secrets'),
      AGENTHUB_SECRET_ENV: 'ENV_KEY', ENV_KEY: envSecret,
      AGENTHUB_SCROLLBACK_PUT_URL: callback + '/objects/scrollback',
      AGENTHUB_TRANSCRIPT_PUT_URL: callback + '/objects/transcript' },
    stdio: ['ignore', 'pipe', 'pipe']
  });
  let logs = '';
  child.stdout.on('data', data => { logs += data; });
  child.stderr.on('data', data => { logs += data; });
  const masked = text => {
    assert.ok(text.includes('[secret:FILE_KEY]'), text);
    assert.ok(text.includes('[secret:ENV_KEY]'), text);
    assert.ok(!text.includes(fileSecret) && !text.includes(envSecret), text);
  };
  try {
    for (let i = 0; i < 100 && !logs.includes('listening'); i++) await sleep(50);
    assert.ok(logs.includes('listening'), logs);
    const get = from => fetch('http://127.0.0.1:' + port + '/agenthub/transcript?from=' + from,
      { headers: { 'X-Agent-Token': token } });
    const response = await get(0);
    assert.equal(response.status, 200);
    masked(await response.text());
    assert.equal(response.headers.get('x-transcript-next'), String(Buffer.byteLength(raw)));
    await sleep(300);
    const socket = new WebSocket('ws://127.0.0.1:' + port);
    const [replay] = await once(socket, 'message');
    socket.close();
    masked(replay.toString());
    console.log('PASS live HTTP + pre-tick real PTY/WebSocket replay; raw byte cursor preserved');
    fs.appendFileSync(path.join(root, 'native.jsonl'), line('next ' + fileSecret + ' ' + envSecret));
    masked(await (await get(Buffer.byteLength(raw))).text());
    assert.equal(fs.readFileSync(path.join(root, 'native.jsonl'), 'utf8').startsWith(raw), true);
    console.log('PASS incremental HTTP read; native resume transcript unchanged');
    // Keep the pod alive for an optional real backend reader probe against this HTTP endpoint.
    if (process.env.PROOF_DOTNET) {
      const probe = spawn(process.env.PROOF_DOTNET, ['run', '--project', path.join(__dirname, 'Reader.csproj'), '--', String(port), callback],
        { stdio: 'inherit', env: process.env });
      const [code] = await once(probe, 'exit');
      assert.equal(code, 0, 'backend reader proof');
    }
    for (let i = 0; i < 400 && uploaded.size < 4; i++) await sleep(100);
    for (const key of ['/objects/transcript', '/transcript', '/objects/scrollback', '/scrollback']) {
      assert.ok(uploaded.has(key), key + ': ' + logs);
      masked(uploaded.get(key));
    }
    fs.writeFileSync(path.join(root, 'captured.json'), JSON.stringify(Object.fromEntries(uploaded)));
    console.log('PASS all four real HTTP/curl uploads; captured artifacts: ' + root);
  } finally {
    child.kill('SIGTERM');
    await Promise.race([once(child, 'exit'), sleep(5000)]);
    if (child.exitCode === null) child.kill('SIGKILL');
    hub.closeAllConnections();
    await new Promise(resolve => hub.close(resolve));
  }
}
