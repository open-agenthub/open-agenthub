'use strict';

const assert = require('node:assert/strict');
const fs = require('node:fs');
const os = require('node:os');
const path = require('node:path');
const test = require('node:test');

function tempConfig(contents) {
  const dir = fs.mkdtempSync(path.join(os.tmpdir(), 'agenthub-trust-'));
  const file = path.join(dir, '.claude.json');
  if (contents !== undefined) fs.writeFileSync(file, contents);
  return file;
}

test('workspace trust is pre-accepted for the session workdir', async () => {
  const { trustWorkspace } = await import('../../claude/workspace-trust.mjs');

  assert.deepEqual(trustWorkspace({}, '/workspace/repo'), {
    hasCompletedOnboarding: true,
    projects: { '/workspace/repo': { hasTrustDialogAccepted: true } }
  });
});

test('workspace trust keeps a custom image own settings and other projects', async () => {
  const { writeWorkspaceTrust } = await import('../../claude/workspace-trust.mjs');
  const file = tempConfig(JSON.stringify({
    hasCompletedOnboarding: false,
    theme: 'dark',
    projects: {
      '/workspace': { hasTrustDialogAccepted: false, allowedTools: ['Read'] },
      '/elsewhere': { hasTrustDialogAccepted: true }
    }
  }));

  writeWorkspaceTrust(file, '/workspace');
  const written = JSON.parse(fs.readFileSync(file, 'utf8'));

  assert.equal(written.theme, 'dark');
  assert.equal(written.hasCompletedOnboarding, true);
  assert.deepEqual(written.projects['/workspace'],
    { hasTrustDialogAccepted: true, allowedTools: ['Read'] });
  assert.deepEqual(written.projects['/elsewhere'], { hasTrustDialogAccepted: true });
});

test('workspace trust survives a missing or corrupt config file', async () => {
  const { writeWorkspaceTrust } = await import('../../claude/workspace-trust.mjs');

  for (const contents of [undefined, 'not json at all', '[]']) {
    const file = tempConfig(contents);
    writeWorkspaceTrust(file, '/workspace');
    const written = JSON.parse(fs.readFileSync(file, 'utf8'));
    assert.equal(written.projects['/workspace'].hasTrustDialogAccepted, true);
    assert.equal(written.hasCompletedOnboarding, true);
  }
});

test('the claude image ships the workspace-trust helper', () => {
  const dockerfile = fs.readFileSync(
    path.join(__dirname, '..', '..', 'claude', 'Dockerfile'), 'utf8');
  assert.match(dockerfile, /COPY claude\s+\/opt\/session-agent\/claude/);
});

test('the session API key is approved by its suffix, and only that key', async () => {
  const { approveApiKey } = await import('../../claude/workspace-trust.mjs');
  const key = 'sk-ant-api03-' + 'a'.repeat(60) + 'EXAMPLE-SUFFIX-12345';
  const config = { theme: 'dark', customApiKeyResponses: { approved: ['other-approved-key01'], rejected: ['EXAMPLE-SUFFIX-12345', 'other-rejected-key01'] } };
  assert.deepEqual(approveApiKey(config, key).customApiKeyResponses, {
    approved: ['other-approved-key01', 'EXAMPLE-SUFFIX-12345'],
    rejected: ['other-rejected-key01']
  });
  assert.equal(approveApiKey(config, key).theme, 'dark');
  assert.doesNotMatch(JSON.stringify(approveApiKey({}, key)), /sk-ant-api03/);
  // Twice is the same as once: a restarted entrypoint does not grow the list.
  assert.deepEqual(approveApiKey(approveApiKey({}, key), key).customApiKeyResponses.approved, ['EXAMPLE-SUFFIX-12345']);
  for (const absent of [undefined, '', 'short']) assert.deepEqual(approveApiKey({ theme: 'dark' }, absent), { theme: 'dark' });
});

test('the entrypoint step reads the key from the environment, not from its arguments', () => {
  const file = tempConfig();
  const key = 'sk-ant-api03-' + 'b'.repeat(60) + 'EXAMPLE-SUFFIX-67890';
  require('node:child_process').execFileSync(process.execPath,
    [path.join(__dirname, '../../claude/workspace-trust.mjs'), file, '/workspace'],
    { env: { PATH: process.env.PATH, ANTHROPIC_API_KEY: key } });
  const written = JSON.parse(fs.readFileSync(file, 'utf8'));
  assert.deepEqual(written.customApiKeyResponses.approved, ['EXAMPLE-SUFFIX-67890']);
  assert.ok(!fs.readFileSync(file, 'utf8').includes(key));
  const entrypoint = fs.readFileSync(path.join(__dirname, '../../claude/entrypoint.sh'), 'utf8');
  assert.match(entrypoint, /node "\$RUNTIME\/claude\/workspace-trust\.mjs" "\$HOME\/\.claude\.json" "\$CLAUDE_WORKDIR"\n/);
  // Without an API key nothing is approved.
  const plain = tempConfig();
  require('node:child_process').execFileSync(process.execPath,
    [path.join(__dirname, '../../claude/workspace-trust.mjs'), plain, '/workspace'], { env: { PATH: process.env.PATH } });
  assert.equal(JSON.parse(fs.readFileSync(plain, 'utf8')).customApiKeyResponses, undefined);
});
