'use strict';

const assert = require('node:assert/strict');
const fs = require('node:fs');
const os = require('node:os');
const path = require('node:path');
const test = require('node:test');

const runtimeDir = path.join(__dirname, '..', '..');

function tempAgentDir() {
  return fs.mkdtempSync(path.join(os.tmpdir(), 'agenthub-openclaw-prompt-'));
}

test('the caller system prompt is appended, never substituted for OpenClaw own', async () => {
  const { writeOpenClawSystemPrompt } = await import('../../openclaw/session-prompt.mjs');
  const agentDir = tempAgentDir();

  const written = writeOpenClawSystemPrompt(agentDir, 'You review, you do not commit.');

  // APPEND_SYSTEM.md is concatenated onto the base prompt; SYSTEM.md would replace it and take
  // OpenClaw's own tool and gateway instructions with it.
  assert.equal(written, path.join(agentDir, 'APPEND_SYSTEM.md'));
  assert.match(fs.readFileSync(written, 'utf8'), /You review, you do not commit\./);
  assert.equal(fs.existsSync(path.join(agentDir, 'SYSTEM.md')), false);
});

test('an empty system prompt removes a file restored from an earlier run of the session', async () => {
  const { writeOpenClawSystemPrompt } = await import('../../openclaw/session-prompt.mjs');
  const agentDir = tempAgentDir();
  const filePath = path.join(agentDir, 'APPEND_SYSTEM.md');

  for (const value of ['', '  ', undefined]) {
    fs.writeFileSync(filePath, 'instructions from a previous run\n');
    assert.equal(writeOpenClawSystemPrompt(agentDir, value), null);
    assert.equal(fs.existsSync(filePath), false);
  }
});

test('the openclaw entrypoint writes the prompt into the agent directory it just created', () => {
  const entrypoint = fs.readFileSync(path.join(runtimeDir, 'openclaw', 'entrypoint.sh'), 'utf8');
  const agentDirExport = entrypoint.indexOf('export OPENCLAW_AGENT_DIR=');
  const sessionPrompt = entrypoint.indexOf('openclaw/session-prompt.mjs');

  assert.ok(agentDirExport >= 0 && sessionPrompt > agentDirExport);
  assert.match(entrypoint,
    /node "\$RUNTIME\/openclaw\/session-prompt\.mjs" "\$OPENCLAW_AGENT_DIR"/);
});
