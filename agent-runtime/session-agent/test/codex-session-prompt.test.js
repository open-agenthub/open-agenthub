'use strict';

const assert = require('node:assert/strict');
const fs = require('node:fs');
const os = require('node:os');
const path = require('node:path');
const test = require('node:test');

const runtimeDir = path.join(__dirname, '..', '..');

function tempHome() {
  return fs.mkdtempSync(path.join(os.tmpdir(), 'agenthub-codex-prompt-'));
}

test('the caller system prompt becomes the global project doc, not a replaced base prompt', async () => {
  const { writeCodexSystemPrompt } = await import('../../codex/session-prompt.mjs');
  const home = tempHome();

  const written = writeCodexSystemPrompt(home, '  You review, you do not commit.  ');

  assert.equal(written, path.join(home, 'AGENTS.md'));
  const body = fs.readFileSync(written, 'utf8');
  assert.match(body, /You review, you do not commit\./);
  // Trimmed, so stray whitespace from an API caller does not end up in the prompt.
  assert.doesNotMatch(body, / {2}You review/);
});

test('an empty system prompt removes a doc restored from an earlier run of the session', async () => {
  const { writeCodexSystemPrompt } = await import('../../codex/session-prompt.mjs');
  const home = tempHome();
  const docPath = path.join(home, 'AGENTS.md');
  // $CODEX_HOME is inside the state dir, so the tar can bring back a previous incarnation's file.
  fs.writeFileSync(docPath, 'instructions from a previous run\n');

  for (const value of ['', '   ', undefined, null]) {
    fs.writeFileSync(docPath, 'instructions from a previous run\n');
    assert.equal(writeCodexSystemPrompt(home, value), null);
    assert.equal(fs.existsSync(docPath), false);
  }
  // Removing an already absent file must not throw — the common case is a session without one.
  assert.equal(writeCodexSystemPrompt(home, ''), null);
});

test('project trust is appended so the MCP tables already in config.toml survive', async () => {
  const { trustCodexProject } = await import('../../codex/session-prompt.mjs');
  const home = tempHome();
  const configPath = path.join(home, 'config.toml');
  const existing = 'cli_auth_credentials_store = "file"\n\n[mcp_servers.agenthub_files]\n';
  fs.writeFileSync(configPath, existing);

  trustCodexProject(configPath, '/workspace/repo');
  const toml = fs.readFileSync(configPath, 'utf8');

  assert.ok(toml.startsWith(existing));
  assert.match(toml, /\n\[projects\."\/workspace\/repo"\]\ntrust_level = "trusted"\n/);
});

test('a path with a quote or backslash cannot break out of the trust table key', async () => {
  const { trustCodexProject } = await import('../../codex/session-prompt.mjs');
  const home = tempHome();
  const configPath = path.join(home, 'config.toml');
  fs.writeFileSync(configPath, '');

  trustCodexProject(configPath, '/workspace/we"ird\\dir');

  assert.match(fs.readFileSync(configPath, 'utf8'),
    /\[projects\."\/workspace\/we\\"ird\\\\dir"\]/);
});

test('the codex entrypoint trusts the workdir and writes the prompt after config.toml is built', () => {
  const entrypoint = fs.readFileSync(path.join(runtimeDir, 'codex', 'entrypoint.sh'), 'utf8');
  const mcpAppend = entrypoint.indexOf('codex/mcp-config.js" --builtin');
  const sessionPrompt = entrypoint.indexOf('codex/session-prompt.mjs');

  assert.ok(mcpAppend >= 0 && sessionPrompt >= 0);
  // Appending before the MCP tables were rendered would put trust_level inside the last of them.
  assert.ok(sessionPrompt > mcpAppend);
  assert.match(entrypoint,
    /node "\$RUNTIME\/codex\/session-prompt\.mjs" "\$CODEX_HOME" "\$CODEX_HOME\/config\.toml" "\$CODEX_WORKDIR"/);
  assert.match(entrypoint, /CODEX_WORKDIR="\$\{AGENTHUB_WORKDIR:-\/workspace\}"/);
});
