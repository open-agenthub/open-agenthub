'use strict';

// What the session agent uploads for other people to read carries no secret value
// (docs/session-secrets.md): the scrollback to S3 and to the hub, and the transcript to the hub.

const test = require('node:test');
const assert = require('node:assert/strict');
const { createHarness, tick } = require('./common-server-harness');

test('common transport masks secret values in the scrollback and transcript it uploads', async () => {
  const transcript = '/home/agent/.test-agent/t.jsonl';
  const harness = createHarness({
    AGENTHUB_CALLBACK_URL: 'https://backend.invalid/internal/session',
    AGENTHUB_CALLBACK_TOKEN: 'synthetic-callback-token',
    AGENTHUB_SCROLLBACK_PUT_URL: 'https://storage.invalid/scrollback',
    AGENTHUB_SECRETS_DIR: '/home/agent/.secrets',
    AGENTHUB_SECRET_ENV: 'DEPLOY_TOKEN',
    DEPLOY_TOKEN: 'env-secret-0123'
  }, { findTranscript: () => transcript }, {
    files: {
      '/home/agent/.secrets/API_TOKEN': 'file-secret-4567',
      [transcript]: JSON.stringify({ text: 'file-secret-4567 and env-secret-0123' }) + '\n'
    }
  });

  harness.terminals[0].emitData('file-secret-4567\r\nenv-secret-0123\r\n');
  harness.intervals[0].callback();
  await tick();
  await tick();

  const scrollbackFile = harness.writes.find(w => w.file === '/tmp/scrollback.log');
  assert.equal(scrollbackFile.data, '[secret:API_TOKEN]\r\n[secret:DEPLOY_TOKEN]\r\n');
  const backup = harness.requests.find(r => r.url.endsWith('/scrollback') && r.options.method === 'PUT');
  assert.equal(backup.options.body, '[secret:API_TOKEN]\r\n[secret:DEPLOY_TOKEN]\r\n');
  const toHub = harness.requests.find(r => r.url.endsWith('/transcript'));
  assert.equal(toHub.options.body, JSON.stringify({ text: '[secret:API_TOKEN] and [secret:DEPLOY_TOKEN]' }) + '\n');
});
