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
