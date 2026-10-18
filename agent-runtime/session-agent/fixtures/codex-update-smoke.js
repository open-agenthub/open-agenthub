'use strict';

// Manual Linux smoke: CODEX_BIN=/absolute/path/to/codex PROOT_BIN=/path/to/proot node this-file.
// Needs node-pty and openssl. proot maps a scratch requirements file over /etc/codex without
// changing the host's managed policy; both control and treatment use the same real CLI.
const assert = require('node:assert/strict');
const fs = require('node:fs');
const os = require('node:os');
const path = require('node:path');
const http = require('node:http');
const https = require('node:https');
const { execFileSync } = require('node:child_process');
const pty = require(process.env.NODE_PTY_MODULE || 'node-pty');

const codex = process.env.CODEX_BIN;
const proot = process.env.PROOT_BIN;
assert.ok(codex && path.isAbsolute(codex), 'CODEX_BIN must be an absolute path');
assert.ok(proot && path.isAbsolute(proot), 'PROOT_BIN must be an absolute path');
const root = fs.mkdtempSync(path.join(os.tmpdir(), 'codex-update-smoke-'));
const home = path.join(root, 'home');
const work = path.join(root, 'work');
const requirements = path.join(root, 'requirements.toml');
const managedRequirements = fs.readFileSync(path.join(__dirname, '../../codex/requirements.toml'), 'utf8');
assert.match(managedRequirements, /^check_for_update_on_startup = false$/m);
const requests = [];
const sockets = new Set();
const cleanEnv = Object.fromEntries(Object.entries(process.env).filter(([key]) => !key.startsWith('CODEX_')));
const openssl = args => execFileSync('openssl', args, { cwd: root, stdio: 'ignore' });

async function run(label, managed, expectPopup) {
  fs.writeFileSync(requirements, managed ? managedRequirements : managedRequirements.replace(/^check_for_update_on_startup = false$/m, 'check_for_update_on_startup = true'));
  // Deliberately opt in at both lower-precedence layers. Neither may undo managed policy.
  fs.writeFileSync(path.join(home, '.codex/config.toml'),
    `check_for_update_on_startup = true\n[projects.${JSON.stringify(work)}]\ntrust_level = "trusted"\n`);
  const before = requests.length;
  let output = '';
  const child = pty.spawn(proot, ['-b', `${requirements}:/etc/codex/requirements.toml`, codex,
    '--no-alt-screen', '--no-daemon', '-c', 'check_for_update_on_startup=true'], {
    name: 'xterm-256color', cols: 120, rows: 40, cwd: work,
    env: { ...cleanEnv, HOME: home, CODEX_HOME: path.join(home, '.codex'), TERM: 'xterm-256color',
      HTTPS_PROXY: `http://127.0.0.1:${proxy.address().port}`, HTTP_PROXY: '', ALL_PROXY: '',
      NO_PROXY: '', https_proxy: '', http_proxy: '', all_proxy: '', no_proxy: '',
      CODEX_CA_CERTIFICATE: path.join(root, 'ca.pem') }
  });
  const exited = new Promise(resolve => child.onExit(resolve));
  child.onData(data => {
    output += data;
    if (data.includes('\x1b[6n')) child.write('\x1b[1;1R');
    if (data.includes('\x1b[c')) child.write('\x1b[?1;2c');
  });
  try {
    await new Promise(resolve => setTimeout(resolve, 8000));
  } finally {
    // proot can keep tracing after SIGTERM; kill the owned group, including the npm wrapper.
    try { process.kill(-child.pid, 'SIGKILL'); } catch (error) {
      if (error.code !== 'ESRCH') throw error;
    }
    await exited;
  }
  const popup = /Update now \(runs/.test(output);
  const updateRequests = requests.slice(before).filter(url =>
    url === '/repos/openai/codex/releases/latest' || url === '/@openai%2fcodex');
  assert.equal(popup, expectPopup, `${label}: ${output}`);
  if (managed) {
    assert.deepEqual(updateRequests, [], `${label}: updater still contacted the fake service`);
    assert.match(output, /Ask Codex|Codex.*anything/, `${label}: CLI never reached its composer`);
  }
  console.log(`${label}: popup=${popup}, updateRequests=${JSON.stringify(updateRequests)}`);
}

let service;
const proxy = http.createServer();
proxy.on('connection', socket => {
  sockets.add(socket);
  socket.on('close', () => sockets.delete(socket));
});
proxy.on('connect', (req, socket, head) => {
  if (!['api.github.com:443', 'registry.npmjs.org:443'].includes(req.url)) {
    socket.end('HTTP/1.1 502 Bad Gateway\r\n\r\n');
    return;
  }
  socket.write('HTTP/1.1 200 Connection Established\r\n\r\n');
  if (head.length) socket.unshift(head);
  service.emit('connection', socket);
});

(async () => {
  try {
    fs.mkdirSync(path.join(home, '.codex'), { recursive: true });
    fs.mkdirSync(work);
    fs.writeFileSync(path.join(home, '.codex/auth.json'), JSON.stringify({
      auth_mode: 'apikey', OPENAI_API_KEY: 'sk-test-not-real'
    }));
    openssl(['req', '-x509', '-newkey', 'rsa:2048', '-nodes', '-keyout', 'ca-key.pem',
      '-out', 'ca.pem', '-days', '1', '-subj', '/CN=Scratch-CA']);
    openssl(['req', '-newkey', 'rsa:2048', '-nodes', '-keyout', 'key.pem', '-out', 'leaf.csr',
      '-subj', '/CN=api.github.com']);
    fs.writeFileSync(path.join(root, 'leaf.ext'),
      'subjectAltName=DNS:api.github.com,DNS:registry.npmjs.org\nbasicConstraints=CA:FALSE\nextendedKeyUsage=serverAuth\n');
    openssl(['x509', '-req', '-in', 'leaf.csr', '-CA', 'ca.pem', '-CAkey', 'ca-key.pem',
      '-CAcreateserial', '-out', 'cert.pem', '-days', '1', '-extfile', 'leaf.ext']);
    service = https.createServer({ key: fs.readFileSync(path.join(root, 'key.pem')),
      cert: fs.readFileSync(path.join(root, 'cert.pem')) }, (req, res) => {
      requests.push(req.url);
      let body;
      if (req.url === '/repos/openai/codex/releases/latest') body = { tag_name: 'rust-v99.0.0' };
      else if (req.url === '/@openai%2fcodex') body = { 'dist-tags': { latest: '99.0.0' },
        versions: { '99.0.0': { dist: { tarball: 'https://example.com/codex.tgz', integrity: 'sha512-test' } } } };
      else { res.writeHead(404).end(); return; }
      res.setHeader('Content-Type', 'application/json');
      res.end(JSON.stringify(body));
    });
    await new Promise(resolve => proxy.listen(0, '127.0.0.1', resolve));
    await run('before-fetch', false, false);
    const version = JSON.parse(fs.readFileSync(path.join(home, '.codex/version.json'), 'utf8'));
    assert.equal(version.latest_version, '99.0.0');
    assert.ok(requests.includes('/@openai%2fcodex'));
    await run('before-popup', false, true);
    await run('managed-cached-user-and-cli-optin', true, false);
    fs.unlinkSync(path.join(home, '.codex/version.json'));
    await run('managed-empty-cache', true, false);
  } finally {
    for (const socket of sockets) socket.destroy();
    proxy.close();
    service?.close();
    fs.rmSync(root, { recursive: true, force: true });
  }
})().catch(error => { console.error(error); process.exitCode = 1; });
