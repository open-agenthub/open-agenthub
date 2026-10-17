'use strict';

// What the session agent uploads for other people to read carries no secret value
// (docs/session-secrets.md): the scrollback to S3 and to the hub, the transcript to S3 and to the hub.

const test = require('node:test');
const assert = require('node:assert/strict');
const { createHarness, tick } = require('./common-server-harness');

const transcript = '/home/agent/.test-agent/t.jsonl';

function harnessWithSecrets(extraEnv = {}) {
  return createHarness({
    AGENTHUB_CALLBACK_URL: 'https://backend.invalid/internal/session',
    AGENTHUB_CALLBACK_TOKEN: 'synthetic-callback-token',
    AGENTHUB_SCROLLBACK_PUT_URL: 'https://storage.invalid/scrollback',
    AGENTHUB_TRANSCRIPT_PUT_URL: 'https://storage.invalid/transcript',
    AGENTHUB_SECRETS_DIR: '/home/agent/.secrets',
    AGENTHUB_SECRET_ENV: 'DEPLOY_TOKEN',
    DEPLOY_TOKEN: 'env-secret-0123',
    ...extraEnv
  }, { findTranscript: () => transcript }, {
    files: {
      '/home/agent/.secrets/API_TOKEN': 'file-secret-4567',
      [transcript]: JSON.stringify({ text: 'file-secret-4567 and env-secret-0123' }) + '\n'
    }
  });
}

test('common transport masks secret values in the scrollback and transcript it uploads', async () => {
  const harness = harnessWithSecrets();

  // Split across two chunks on purpose: the PTY hands output over in pieces of its choosing.
  harness.terminals[0].emitData('file-secret-45');
  harness.terminals[0].emitData('67\r\nenv-secret-0123\r\n');
  harness.intervals[0].callback();
  await tick();

  const masked = '[secret:API_TOKEN]\r\n[secret:DEPLOY_TOKEN]\r\n';
  assert.equal(harness.writes.find(w => w.file === '/tmp/scrollback.log').data, masked);
  const backup = harness.requests.find(r => r.url.endsWith('/scrollback') && r.options.method === 'PUT');
  assert.equal(backup.options.body, masked);

  // The object storage copy goes to curl on stdin; the CLI's file itself is never written.
  const upload = harness.pipeSpawns.find(s => s.cmd === 'curl');
  assert.deepEqual(upload.args.slice(-3), ['-T', '-', 'https://storage.invalid/transcript']);
  const curl = harness.children[harness.pipeSpawns.indexOf(upload)];
  const maskedTranscript = JSON.stringify({ text: '[secret:API_TOKEN] and [secret:DEPLOY_TOKEN]' }) + '\n';
  assert.deepEqual(curl.stdinWrites, [maskedTranscript]);
  assert.equal(curl.stdinEnded, true);
  assert.equal(harness.writes.some(w => w.file === transcript), false);

  curl.emitExit(0);
  await tick();
  const toHub = harness.requests.find(r => r.url.endsWith('/transcript'));
  assert.equal(toHub.options.body, maskedTranscript);
});

test('common transport uploads the transcript file as before when there is nothing to mask', async () => {
  const harness = harnessWithSecrets({ AGENTHUB_SECRETS_DIR: '/nowhere', AGENTHUB_SECRET_ENV: '' });
  harness.terminals[0].emitData('plain output');
  harness.intervals[0].callback();
  await tick();

  const curl = harness.commands.find(c => c.file === 'curl' && c.args.at(-1) === 'https://storage.invalid/transcript');
  assert.deepEqual(curl.args.slice(-3), ['-T', transcript, 'https://storage.invalid/transcript']);
  assert.equal(harness.pipeSpawns.some(s => s.cmd === 'curl'), false);
});
