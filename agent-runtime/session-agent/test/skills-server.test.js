const assert = require('node:assert/strict');
const fs = require('node:fs');
const os = require('node:os');
const path = require('node:path');
const { execFileSync } = require('node:child_process');
const test = require('node:test');

function workdir() {
  const dir = fs.mkdtempSync(path.join(os.tmpdir(), 'skills-server-'));
  test.after(() => fs.rmSync(dir, { recursive: true, force: true }));
  return dir;
}

function write(file, content) {
  fs.mkdirSync(path.dirname(file), { recursive: true });
  fs.writeFileSync(file, content);
}

/** A hub that records what it was asked and answers in the hub's plain-text shape. */
function fakeHub(options = {}) {
  const calls = [];
  return {
    calls,
    async rpc(message) {
      calls.push(message);
      if (message.method === 'tools/list') {
        return {
          jsonrpc: '2.0',
          id: message.id,
          result: {
            tools: [
              { name: 'upload_skill', description: 'Save a skill.', inputSchema: { properties: { name: {} } } },
              { name: 'get_skill', description: 'Read a skill.', inputSchema: { properties: { name: {} } } }
            ]
          }
        };
      }
      if (message.method === 'initialize') {
        return { jsonrpc: '2.0', id: message.id, result: { instructions: 'Use it proactively.' } };
      }
      if (options.toolError) {
        return {
          jsonrpc: '2.0', id: message.id,
          result: { content: [{ type: 'text', text: 'Skill name must be kebab-case.' }], isError: true }
        };
      }
      return {
        jsonrpc: '2.0', id: message.id,
        result: { content: [{ type: 'text', text: 'Saved deploy as v4 in the project library.' }] }
      };
    },
    async bundle(nameOrId, version) {
      calls.push({ bundle: nameOrId, version });
      return options.archive;
    }
  };
}

test('tools/list gains path and out_dir without losing the hub wording', async () => {
  const { createProxy } = await import('../../skills/server.mjs');
  const hub = fakeHub();
  const response = await createProxy({ client: hub }).handle(
    { jsonrpc: '2.0', id: 1, method: 'tools/list' });

  const upload = response.result.tools.find(t => t.name === 'upload_skill');
  const get = response.result.tools.find(t => t.name === 'get_skill');
  assert.match(upload.inputSchema.properties.path.description, /skill directory/);
  assert.match(upload.description, /^Save a skill\./);
  assert.match(get.inputSchema.properties.out_dir.description, /never passes through your context/);
  // The hub's own arguments survive.
  assert.ok(upload.inputSchema.properties.name);
});

test('initialize keeps the hub instructions and adds the local capability', async () => {
  const { createProxy } = await import('../../skills/server.mjs');
  const response = await createProxy({ client: fakeHub() }).handle(
    { jsonrpc: '2.0', id: 1, method: 'initialize', params: {} });

  assert.match(response.result.instructions, /Use it proactively\./);
  assert.match(response.result.instructions, /upload_skill takes a local path/);
});

test('upload_skill(path) sends the files inline and never asks the hub for the path', async () => {
  const { createProxy } = await import('../../skills/server.mjs');
  const dir = workdir();
  write(path.join(dir, 'SKILL.md'), '# Deploy runbook');
  write(path.join(dir, 'scripts/check.sh'), '#!/bin/sh\necho größe');
  const hub = fakeHub();

  const response = await createProxy({ client: hub }).handle({
    jsonrpc: '2.0', id: 7, method: 'tools/call',
    params: { name: 'upload_skill', arguments: { name: 'deploy', path: dir, comment: 'first cut' } }
  });

  const forwarded = hub.calls[0].params.arguments;
  assert.equal(forwarded.path, undefined);
  assert.equal(forwarded.content, '# Deploy runbook');
  assert.deepEqual(forwarded.files, [{ path: 'scripts/check.sh', content: '#!/bin/sh\necho größe' }]);
  assert.equal(forwarded.comment, 'first cut');

  const text = response.result.content[0].text;
  assert.match(text, /Saved deploy as v4/);
  assert.match(text, /1 file/);
  assert.match(text, /none of it passed through your context/);
});

test('upload_skill(path) with an explicit content keeps the argument', async () => {
  const { createProxy } = await import('../../skills/server.mjs');
  const dir = workdir();
  write(path.join(dir, 'SKILL.md'), '# on disk');
  write(path.join(dir, 'a.md'), 'A');
  const hub = fakeHub();

  await createProxy({ client: hub }).handle({
    jsonrpc: '2.0', id: 1, method: 'tools/call',
    params: { name: 'upload_skill', arguments: { name: 'deploy', path: dir, content: '# from the call' } }
  });

  assert.equal(hub.calls[0].params.arguments.content, '# from the call');
});

test('upload_skill(path) for a lone script without content explains what is missing', async () => {
  const { createProxy } = await import('../../skills/server.mjs');
  const dir = workdir();
  write(path.join(dir, 'helper.ps1'), 'Write-Host "x"');
  const hub = fakeHub();

  const response = await createProxy({ client: hub }).handle({
    jsonrpc: '2.0', id: 1, method: 'tools/call',
    params: { name: 'upload_skill', arguments: { name: 'deploy', path: path.join(dir, 'helper.ps1') } }
  });

  assert.equal(response.result.isError, true);
  assert.match(response.result.content[0].text, /No SKILL\.md found/);
  assert.equal(hub.calls.length, 0, 'an upload that cannot work must not reach the hub');
});

test('a missing path is reported as a readable error, not a backend code', async () => {
  const { createProxy } = await import('../../skills/server.mjs');
  const hub = fakeHub();

  const response = await createProxy({ client: hub }).handle({
    jsonrpc: '2.0', id: 1, method: 'tools/call',
    params: { name: 'upload_skill', arguments: { name: 'deploy', path: '/nowhere/at/all' } }
  });

  assert.equal(response.result.isError, true);
  assert.match(response.result.content[0].text, /path not found: \/nowhere\/at\/all/);
});

test('get_skill(out_dir) writes the skill to disk and reports only the paths', async () => {
  const { createProxy } = await import('../../skills/server.mjs');
  const dir = workdir();
  write(path.join(dir, 'src/SKILL.md'), '# bundled skill');
  write(path.join(dir, 'src/scripts/check.sh'), 'true');
  execFileSync('tar', ['czf', 'bundle.tar.gz', 'SKILL.md', 'scripts'], { cwd: path.join(dir, 'src') });
  const archive = fs.readFileSync(path.join(dir, 'src', 'bundle.tar.gz'));
  const hub = fakeHub({ archive });
  const target = path.join(dir, 'out');

  const response = await createProxy({ client: hub }).handle({
    jsonrpc: '2.0', id: 3, method: 'tools/call',
    params: { name: 'get_skill', arguments: { name: 'deploy', out_dir: target } }
  });

  assert.equal(hub.calls[0].params.arguments.out_dir, undefined);
  assert.deepEqual(hub.calls[1], { bundle: 'deploy', version: undefined });
  assert.equal(fs.readFileSync(path.join(target, 'scripts', 'check.sh'), 'utf8'), 'true');

  const text = response.result.content[0].text;
  assert.match(text, /Written to/);
  assert.match(text, /scripts\/check\.sh/);
  // The file content stays out of the reply; only SKILL.md (which the hub inlines) is there.
  assert.doesNotMatch(text, /^true$/m);
});

test('get_skill rejects file and out_dir together instead of picking one', async () => {
  const { createProxy } = await import('../../skills/server.mjs');
  const hub = fakeHub();

  const response = await createProxy({ client: hub }).handle({
    jsonrpc: '2.0', id: 1, method: 'tools/call',
    params: { name: 'get_skill', arguments: { name: 'deploy', file: 'a.md', out_dir: '/tmp/x' } }
  });

  assert.equal(response.result.isError, true);
  assert.match(response.result.content[0].text, /either 'file'/);
  assert.equal(hub.calls.length, 0);
});

test('a hub tool error passes through and nothing is written', async () => {
  const { createProxy } = await import('../../skills/server.mjs');
  const dir = workdir();
  write(path.join(dir, 'SKILL.md'), '# s');
  const hub = fakeHub({ toolError: true });

  const response = await createProxy({ client: hub }).handle({
    jsonrpc: '2.0', id: 1, method: 'tools/call',
    params: { name: 'upload_skill', arguments: { name: 'Bad Name', path: dir } }
  });

  assert.equal(response.result.isError, true);
  assert.match(response.result.content[0].text, /kebab-case/);
});

test('calls without the local arguments are forwarded untouched', async () => {
  const { createProxy } = await import('../../skills/server.mjs');
  const hub = fakeHub();
  const message = {
    jsonrpc: '2.0', id: 9, method: 'tools/call',
    params: { name: 'search_skills', arguments: { query: 'kubernetes' } }
  };

  const response = await createProxy({ client: hub }).handle(message);

  assert.deepEqual(hub.calls[0], message);
  assert.match(response.result.content[0].text, /Saved deploy/);
});

test('a notification produces no reply', async () => {
  const { createProxy } = await import('../../skills/server.mjs');
  const hub = {
    calls: [],
    async rpc(message) { hub.calls.push(message); return null; }
  };

  assert.equal(await createProxy({ client: hub }).handle(
    { jsonrpc: '2.0', method: 'notifications/initialized' }), null);
  assert.equal(hub.calls.length, 1);
});

test('the stdio loop answers one message per line and holds partial lines', async () => {
  const { run } = await import('../../skills/server.mjs');
  const { Readable, Writable } = require('node:stream');
  const written = [];
  const output = new Writable({
    write(chunk, _encoding, callback) { written.push(chunk.toString()); callback(); }
  });
  const input = Readable.from([
    '{"jsonrpc":"2.0","id":1,"method":"ping"}\n{"jsonrpc":"2.0","id":2,',
    '"method":"ping"}\nnot json\n'
  ]);
  const proxy = {
    handle: async message => message.method === 'ping'
      ? { jsonrpc: '2.0', id: message.id, result: {} }
      : null
  };

  await run(proxy, input, output);

  const lines = written.join('').trim().split('\n').map(JSON.parse);
  assert.deepEqual(lines.map(line => line.id), [1, 2, null]);
  assert.equal(lines[2].error.code, -32700);
});
