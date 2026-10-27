// #306: does the session agent's `ready` mean the CLI keeps what is typed? Runs this checkout's
// common/server.js with the real driver and the pinned CLI in its PTY, subscribes to activity as
// the Workspace does, and types a marker over the agent socket the instant `ready` turns true.
// Reports when that was and whether the marker is in the CLI's input box afterwards.
//
//   node ready-input.cjs <claude|codex|opencode>    (CLIs on PATH; credentials are examples)
'use strict';
const fs = require('node:fs');
const os = require('node:os');
const path = require('node:path');
const http = require('node:http');
const { execFileSync } = require('node:child_process');
const modules = '/opt/session-agent/node_modules';
const { WebSocket } = require(modules + '/ws');
const { Terminal } = require(modules + '/@xterm/headless');

const name = process.argv[2] || 'claude';
const repo = path.resolve(__dirname, '../../..');
const root = fs.mkdtempSync(path.join(os.tmpdir(), 'proof-306-ready-'));
// The runtime as the image lays it out, with the agent's own scratch paths moved into root: in
// a session pod /tmp holds that session's state archive and watcher pid.
fs.cpSync(path.join(repo, 'agent-runtime'), path.join(root, 'agent-runtime'), { recursive: true, filter: s => !s.includes('node_modules') });
fs.symlinkSync(modules, path.join(root, 'agent-runtime/node_modules'));
const serverPath = path.join(root, 'agent-runtime/common/server.js');
let source = fs.readFileSync(serverPath, 'utf8');
for (const file of ['state.tgz', 'scrollback.log', 'agenthub-auth-watcher.pid']) source = source.split('/tmp/' + file).join(path.join(root, file));
fs.writeFileSync(serverPath, source);

const home = path.join(root, 'home');
const work = path.join(root, 'work');
fs.mkdirSync(home); fs.mkdirSync(work); fs.mkdirSync(path.join(home, 'tmp'));
const PORT = 17725;
// Stand-in hub: the callbacks the agent makes at start only need an answer.
http.createServer((req, res) => { req.resume(); res.writeHead(200, { 'content-type': 'application/json' }); res.end('{}'); }).listen(PORT + 1, '127.0.0.1');
const env = Object.fromEntries(Object.entries(process.env).filter(([k]) => !/^(CLAUDE|AGENTHUB|ANTHROPIC|OPENAI|CODEX|OPENCODE|XDG_)/.test(k)));
Object.assign(env, {
  HOME: home, TMPDIR: path.join(home, 'tmp'), TERM: 'xterm-256color', XDG_CONFIG_HOME: home + '/.config', XDG_DATA_HOME: home + '/.local/share', XDG_STATE_HOME: home + '/.local/state',
  AGENTHUB_MODE: 'interactive', AGENTHUB_UI_MODE: 'terminal', AGENTHUB_PORT: String(PORT), AGENTHUB_WORKDIR: work, AGENTHUB_STATE_RESTORED: '0',
  AGENTHUB_SESSION_ID: 'ready-proof', AGENTHUB_CALLBACK_URL: `http://127.0.0.1:${PORT + 1}/internal`, AGENTHUB_CALLBACK_TOKEN: 'example-token',
  AGENTHUB_FILE_ROOT: path.join(root, 'files'), AGENTHUB_SKILLS_SYNC_SECONDS: '0', DISABLE_AUTOUPDATER: '1', OPENCODE_DISABLE_AUTOUPDATE: '1'
});
if (name === 'claude') {
  fs.writeFileSync(path.join(home, '.claude.json'), JSON.stringify({ hasCompletedOnboarding: true, theme: 'dark', projects: { [work]: { hasTrustDialogAccepted: true, hasCompletedProjectOnboarding: true } } }));
}
if (name === 'codex') {
  env.CODEX_HOME = path.join(home, '.codex');
  fs.mkdirSync(env.CODEX_HOME);
  fs.writeFileSync(path.join(env.CODEX_HOME, 'config.toml'), `check_for_update_on_startup = false\n[projects.${JSON.stringify(work)}]\ntrust_level = "trusted"\n`);
  // What the entrypoint does with an API key; an example key, nothing is sent with it here.
  execFileSync('codex', ['login', '--with-api-key'], { input: 'sk-example\n', env, stdio: ['pipe', 'ignore', 'ignore'] });
}

const driver = require(path.join(root, 'agent-runtime', name, 'driver.js'));
const t0 = Date.now();
require(serverPath).createCommonServer({ env, driver });
const marker = 'ready-marker-306';
const terminal = new Terminal({ cols: 120, rows: 40, allowProposedApi: true });
const rows = () => { const b = terminal.buffer.active; return Array.from({ length: terminal.rows }, (_, y) => b.getLine(b.baseY + y)?.translateToString(true) || ''); };
let readyAt = null;
let firstOutput = null;
const connect = () => {
  const socket = new WebSocket(`ws://127.0.0.1:${PORT}/`, 'tty');
  socket.on('error', () => setTimeout(connect, 50));
  socket.on('open', () => {
    socket.send(JSON.stringify({ type: 'resize', cols: 120, rows: 40 }));
    socket.send(JSON.stringify({ type: 'subscribe', channel: 'activity' }));
  });
  socket.on('message', (data, binary) => {
    const text = data.toString();
    if (!binary && text.startsWith('{"type":"agenthub"')) {
      const frame = JSON.parse(text);
      if (frame.subtype === 'activity' && frame.ready && readyAt === null) {
        readyAt = Date.now() - t0;
        socket.send(JSON.stringify({ type: 'input', data: marker }));
        setTimeout(() => {
          // The input box: Claude ❯, Codex ›, OpenCode ┃ — not the PTY's echo before raw mode.
          const box = rows().filter(row => /^\s*(?:❯|›|┃)/.test(row) && row.includes(marker));
          console.log(JSON.stringify({ cli: name, firstOutputMs: firstOutput, readyMs: readyAt, markerInInputBox: box.length > 0, row: box[0]?.trim() || null }));
          fs.rmSync(root, { recursive: true, force: true });
          process.kill(process.pid, 'SIGTERM');
          setTimeout(() => process.exit(0), 3000);
        }, 6000);
      }
      return;
    }
    if (firstOutput === null) firstOutput = Date.now() - t0;
    terminal.write(text);
  });
};
connect();
setTimeout(() => { console.log(JSON.stringify({ cli: name, readyMs: null, note: 'never ready in 40 s', screen: rows().filter(r => r.trim()).slice(-6) })); process.exit(1); }, 40000);
