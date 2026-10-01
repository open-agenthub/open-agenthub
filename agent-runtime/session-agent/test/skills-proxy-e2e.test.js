// Runs the real skills/server.mjs as a child process against a stub hub, driving it over
// stdio the way an agent CLI does. The injected-fake tests above cannot catch what this
// catches: a bad import path, a module the image does not ship, an env variable the proxy
// needs but never reads, or a response that is not valid line-framed JSON.

const assert = require('node:assert/strict');
const fs = require('node:fs');
const http = require('node:http');
const os = require('node:os');
const path = require('node:path');
const { execFileSync, spawn } = require('node:child_process');
const test = require('node:test');

const proxyPath = path.join(__dirname, '..', '..', 'skills', 'server.mjs');

/** A stub of the hub's skill-library endpoints: the MCP POST and the bundle download. */
function startHub(bundle) {
  const received = [];
  const server = http.createServer((request, response) => {
    // includes, not endsWith: the proxy appends ?version= when the agent asked for one.
    if (request.url.includes('/files.tar.gz')) {
      received.push({ method: request.method, url: request.url });
      response.writeHead(200, { 'Content-Type': 'application/gzip' });
      response.end(bundle);
      return;
    }
    let body = '';
    request.on('data', chunk => { body += chunk; });
    request.on('end', () => {
      const message = JSON.parse(body);
      received.push({ message, token: request.headers['x-agent-token'] });
      const result = message.method === 'initialize'
        ? { protocolVersion: '2025-06-18', capabilities: { tools: {} }, instructions: 'Use it proactively.' }
        : message.method === 'tools/list'
          ? { tools: [{ name: 'upload_skill', description: 'Save.', inputSchema: { properties: {} } }] }
          : { content: [{ type: 'text', text: 'Saved deploy as v2 in the personal library.' }] };
      response.writeHead(200, { 'Content-Type': 'application/json' });
      response.end(JSON.stringify({ jsonrpc: '2.0', id: message.id, result }));
    });
  });
  return new Promise(resolve => {
    server.listen(0, '127.0.0.1', () => resolve({
      received,
      url: `http://127.0.0.1:${server.address().port}/internal/sessions/s1`,
      close: () => new Promise(done => server.close(done))
    }));
  });
}

/** Sends the messages one per line and collects the replies. */
function drive(env, messages) {
  const child = spawn(process.execPath, [proxyPath], {
    env: { ...process.env, ...env },
    stdio: ['pipe', 'pipe', 'pipe']
  });
  const replies = [];
  let buffer = '';
  let stderr = '';
  child.stderr.on('data', chunk => { stderr += chunk; });
  return new Promise((resolve, reject) => {
    child.stdout.on('data', chunk => {
      buffer += chunk;
      let newline;
      while ((newline = buffer.indexOf('\n')) >= 0) {
        const line = buffer.slice(0, newline).trim();
        buffer = buffer.slice(newline + 1);
        if (line) replies.push(JSON.parse(line));
      }
    });
    child.on('error', reject);
    child.on('close', code => resolve({ replies, stderr, code }));
    for (const message of messages) child.stdin.write(`${JSON.stringify(message)}\n`);
    // Closing stdin right away is what an agent CLI does at the end of a session, and the
    // proxy has to finish the requests it already read before exiting — if it did not, a
    // reply would be lost here rather than in production where it is much harder to see.
    child.stdin.end();
  });
}

test('the proxy serves initialize, tools/list and a path upload end to end', async () => {
  const dir = fs.mkdtempSync(path.join(os.tmpdir(), 'skills-e2e-'));
  test.after(() => fs.rmSync(dir, { recursive: true, force: true }));
  fs.mkdirSync(path.join(dir, 'skill', 'scripts'), { recursive: true });
  fs.writeFileSync(path.join(dir, 'skill', 'SKILL.md'), '# Deploy\n\nGröße prüfen 🚀');
  fs.writeFileSync(path.join(dir, 'skill', 'scripts', 'check.sh'), '#!/bin/sh\ntrue');
  execFileSync('tar', ['czf', 'bundle.tar.gz', 'SKILL.md', 'scripts'], { cwd: path.join(dir, 'skill') });
  const bundle = fs.readFileSync(path.join(dir, 'skill', 'bundle.tar.gz'));

  const hub = await startHub(bundle);
  try {
    const { replies, stderr, code } = await drive(
      { AGENTHUB_CALLBACK_URL: hub.url, AGENTHUB_CALLBACK_TOKEN: 'secret' },
      [
        { jsonrpc: '2.0', id: 1, method: 'initialize', params: { protocolVersion: '2025-06-18' } },
        { jsonrpc: '2.0', id: 2, method: 'tools/list' },
        {
          jsonrpc: '2.0', id: 3, method: 'tools/call',
          params: {
            name: 'upload_skill',
            arguments: { name: 'deploy', path: path.join(dir, 'skill'), description: 'Deploy runbook' }
          }
        }
      ]);

    assert.equal(code, 0, stderr);
    assert.equal(replies.length, 3);
    assert.match(replies[0].result.instructions, /upload_skill takes a local path/);
    assert.ok(replies[1].result.tools[0].inputSchema.properties.path);

    // The upload reached the hub with the token and with the file content inline, and the
    // path argument never got there.
    const upload = hub.received.find(entry => entry.message?.params?.name === 'upload_skill');
    assert.equal(upload.token, 'secret');
    assert.equal(upload.message.params.arguments.path, undefined);
    assert.equal(upload.message.params.arguments.content, '# Deploy\n\nGröße prüfen 🚀');
    assert.deepEqual(upload.message.params.arguments.files,
      [{ path: 'scripts/check.sh', content: '#!/bin/sh\ntrue' }]);
    assert.match(replies[2].result.content[0].text, /Read from .* \(directory\)/);
  } finally {
    await hub.close();
  }
});

test('the proxy writes a skill to out_dir from the bundle route', async () => {
  const dir = fs.mkdtempSync(path.join(os.tmpdir(), 'skills-e2e-out-'));
  test.after(() => fs.rmSync(dir, { recursive: true, force: true }));
  fs.mkdirSync(path.join(dir, 'src', 'scripts'), { recursive: true });
  fs.writeFileSync(path.join(dir, 'src', 'SKILL.md'), '# bundled');
  fs.writeFileSync(path.join(dir, 'src', 'scripts', 'check.sh'), 'true');
  execFileSync('tar', ['czf', 'bundle.tar.gz', 'SKILL.md', 'scripts'], { cwd: path.join(dir, 'src') });
  const bundle = fs.readFileSync(path.join(dir, 'src', 'bundle.tar.gz'));

  const hub = await startHub(bundle);
  const target = path.join(dir, 'out');
  try {
    const { replies, stderr, code } = await drive(
      { AGENTHUB_CALLBACK_URL: hub.url, AGENTHUB_CALLBACK_TOKEN: 'secret' },
      [{
        jsonrpc: '2.0', id: 1, method: 'tools/call',
        params: { name: 'get_skill', arguments: { name: 'deploy', version: 2, out_dir: target } }
      }]);

    assert.equal(code, 0, stderr);
    assert.equal(fs.readFileSync(path.join(target, 'scripts', 'check.sh'), 'utf8'), 'true');
    assert.match(replies[0].result.content[0].text, /Written to .*out: SKILL\.md and 1 file\(s\)/);
    const download = hub.received.find(entry => entry.url);
    assert.match(download.url, /\/skills\/deploy\/files\.tar\.gz\?version=2$/);
  } finally {
    await hub.close();
  }
});

test('without a callback url the proxy refuses to start rather than failing silently', async () => {
  const { code, stderr } = await drive({ AGENTHUB_CALLBACK_URL: '', AGENTHUB_CALLBACK_TOKEN: '' }, []);
  assert.notEqual(code, 0);
  assert.match(stderr, /skills_backend_not_configured/);
});
