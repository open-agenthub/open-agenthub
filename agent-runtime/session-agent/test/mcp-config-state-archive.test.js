'use strict';

// The generated MCP client configs against the real state archive (#446). Each converter writes
// where its entrypoint writes, the archive is built with the real archiveScript and the real
// driver's excludes, and the gunzipped tar is searched for the header and env values. Those values
// arrive with the session's MCP document at every start, so the archive has no reason to hold them.

const assert = require('node:assert/strict');
const { execFileSync } = require('node:child_process');
const fs = require('node:fs');
const os = require('node:os');
const path = require('node:path');
const test = require('node:test');
const zlib = require('node:zlib');

const { archiveScript } = require('../../common/state-archive');

const runtimeDir = path.join(__dirname, '..', '..');
const HEADER_VALUE = 'hdr-secret-value-0123456789';
const ENV_VALUE = 'env-secret-value-9876543210';
const HUB_TOKEN = 'hub-callback-token-0123456789';

const MCP_DOCUMENT = {
  mcpServers: {
    remote: { url: 'https://mcp.example.com/mcp', headers: { 'X-Api-Key': HEADER_VALUE } },
    local: { command: 'node', args: ['/srv/tool.mjs'], env: { API_TOKEN: ENV_VALUE } },
    // The hub's own skill-library entry, as it reaches a runtime with the local proxy switched off.
    'skill-library': {
      type: 'http', url: 'https://hub.example.com/internal/sessions/s1/mcp',
      headers: { 'X-Agent-Token': HUB_TOKEN }
    }
  }
};

function setup(t) {
  const home = fs.mkdtempSync(path.join(os.tmpdir(), 'agenthub-mcp-archive-'));
  t.after(() => fs.rmSync(home, { recursive: true, force: true }));
  const mcpPath = path.join(home, 'mcp.json');
  fs.writeFileSync(mcpPath, JSON.stringify(MCP_DOCUMENT));
  return { home, mcpPath };
}

function archived(home, driver) {
  const archive = path.join(home, 'state.tgz');
  execFileSync('/bin/sh', ['-c', archiveScript(driver), 'sh', archive], { cwd: home });
  return zlib.gunzipSync(fs.readFileSync(archive));
}

function assertNoValues(raw) {
  for (const value of [HEADER_VALUE, ENV_VALUE, HUB_TOKEN]) {
    assert.equal(raw.includes(Buffer.from(value)), false, `archive contains ${value}`);
  }
}

test('Codex: config.toml with MCP header values stays out of the archive', t => {
  const { home, mcpPath } = setup(t);
  const toml = execFileSync(process.execPath, [path.join(runtimeDir, 'codex', 'mcp-config.js'), mcpPath],
    { env: { ...process.env, AGENTHUB_SKILLS_MCP_ENABLED: '0' }, encoding: 'utf8' });
  fs.mkdirSync(path.join(home, '.codex'));
  fs.writeFileSync(path.join(home, '.codex', 'config.toml'), toml);
  fs.writeFileSync(path.join(home, '.codex', 'config.toml.bak'), toml);
  fs.writeFileSync(path.join(home, '.codex', 'history.jsonl'), '{"text":"kept"}\n');
  // The precondition, so the test cannot pass by the converter dropping the values.
  assert.ok(toml.includes(HEADER_VALUE));

  const raw = archived(home, require('../../codex/driver'));
  assertNoValues(raw);
  // The rest of the state directory is still archived — resume depends on it.
  assert.ok(raw.includes(Buffer.from('"text":"kept"')));
});

test('Cursor: mcp.json with MCP header values stays out of the archive', t => {
  const { home, mcpPath } = setup(t);
  const json = execFileSync(process.execPath, [path.join(runtimeDir, 'cursor', 'mcp-config.js'), mcpPath],
    { encoding: 'utf8' });
  fs.mkdirSync(path.join(home, '.cursor'));
  fs.writeFileSync(path.join(home, '.cursor', 'mcp.json'), json);
  fs.writeFileSync(path.join(home, '.cursor', 'mcp.json.bak'), json);
  fs.writeFileSync(path.join(home, '.cursor', 'cli-config.json'), '{"kept":true}');
  assert.ok(json.includes(HEADER_VALUE));

  const raw = archived(home, require('../../cursor/driver'));
  assertNoValues(raw);
  assert.ok(raw.includes(Buffer.from('"kept":true')));
});

test('OpenClaw: openclaw.json is archived but holds references, not values', t => {
  const { home, mcpPath } = setup(t);
  const mcp = require('../../openclaw/mcp-config');
  const configPath = path.join(home, '.openclaw', 'openclaw.json');
  const envFile = path.join(home, 'tmp', 'openclaw-mcp.env');
  mcp.writeConfig(configPath, mcpPath, [], { RUNTIME: path.join(home, 'none') }, envFile);

  // OpenClaw rotates copies of the config on every write; one restored from an archive written
  // before the fix still holds the literal values.
  fs.writeFileSync(configPath + '.bak', JSON.stringify(MCP_DOCUMENT));
  fs.writeFileSync(configPath + '.bak.3', JSON.stringify(MCP_DOCUMENT));

  // openclaw.json stays in the archive: it also carries the agent's own settings and the servers
  // it added with `openclaw mcp add`, which a resume has to bring back.
  const raw = archived(home, require('../../openclaw/driver'));
  assertNoValues(raw);
  assert.ok(raw.includes(Buffer.from('"remote"')));

  const written = JSON.parse(fs.readFileSync(configPath, 'utf8')).mcp.servers;
  assert.match(written.remote.headers['X-Api-Key'], /^\$\{AGENTHUB_MCP_VALUE_\d+\}$/);
  assert.match(written.local.env.API_TOKEN, /^\$\{AGENTHUB_MCP_VALUE_\d+\}$/);

  // The values travel in a file outside the state directory, which the entrypoint sources.
  const exported = execFileSync('/bin/bash', ['-c', 'set -a; . "$1"; env', 'bash', envFile], { encoding: 'utf8' });
  const variable = name => new RegExp('^' + name + '=(.*)$', 'm').exec(exported)?.[1];
  assert.equal(variable(written.remote.headers['X-Api-Key'].slice(2, -1)), HEADER_VALUE);
  assert.equal(variable(written.local.env.API_TOKEN.slice(2, -1)), ENV_VALUE);
  assert.equal(fs.statSync(envFile).mode & 0o777, 0o600);
});

test('each entrypoint deletes restored copies of its MCP config, but not the config itself', t => {
  // An archive written before #446 brings those copies back with literal values in them.
  const cases = [
    ['codex', /^rm -f "\$CODEX_HOME"\/config\.toml\?\*$/m, 'CODEX_HOME', 'config.toml', ['config.toml.bak', 'config.toml.1']],
    ['cursor', /^rm -f "\$CURSOR_CONFIG_DIR"\/mcp\.json\?\*$/m, 'CURSOR_CONFIG_DIR', 'mcp.json', ['mcp.json.bak', 'mcp.json~']],
    ['openclaw', /^rm -f "\$OPENCLAW_STATE_DIR"\/openclaw\.json\.bak\*$/m, 'OPENCLAW_STATE_DIR', 'openclaw.json', ['openclaw.json.bak', 'openclaw.json.bak.4']]
  ];
  for (const [runtime, pattern, variable, file, copies] of cases) {
    const entrypoint = fs.readFileSync(path.join(runtimeDir, runtime, 'entrypoint.sh'), 'utf8');
    const line = pattern.exec(entrypoint)?.[0];
    assert.ok(line, runtime);
    const dir = fs.mkdtempSync(path.join(os.tmpdir(), `agenthub-${runtime}-copies-`));
    t.after(() => fs.rmSync(dir, { recursive: true, force: true }));
    for (const name of [file, ...copies]) fs.writeFileSync(path.join(dir, name), HEADER_VALUE);
    execFileSync('/bin/bash', ['-c', line], { env: { ...process.env, [variable]: dir } });
    assert.deepEqual(fs.readdirSync(dir), [file], runtime);
  }
});
