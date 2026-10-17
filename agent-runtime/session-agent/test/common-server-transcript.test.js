'use strict';

// GET /agenthub/transcript, which the hub follows a running session's conversation with, and
// the refresh of the presigned upload urls (#111, docs/transcripts.md).
const assert = require('node:assert/strict');
const test = require('node:test');

const { createHarness, requestHttp, tick } = require('./common-server-harness');

const FILE = '/home/agent/.claude/projects/-workspace-repo/s.jsonl';
const auth = { 'X-Agent-Token': 'correct-token' };

function transcriptHarness(files, environment = {}) {
  return createHarness({ AGENTHUB_CALLBACK_TOKEN: 'correct-token', ...environment },
    { findTranscript: () => (FILE in files ? FILE : null) }, { files });
}

test('transcript route refuses a caller without the callback token', async () => {
  const harness = transcriptHarness({ [FILE]: '{"a":1}\n' });
  const response = await requestHttp(harness, 'GET', '/agenthub/transcript?from=0', { 'X-Agent-Token': 'wrong' });
  assert.equal(response.status, 401);
  assert.equal(response.body.includes('"a"'), false);
});

test('transcript route answers an empty page before the CLI has written its file', async () => {
  const harness = transcriptHarness({});
  const response = await requestHttp(harness, 'GET', '/agenthub/transcript?from=0', auth);
  assert.equal(response.status, 200);
  assert.equal(response.headers['x-transcript-size'], '0');
  assert.equal(response.headers['x-transcript-next'], '0');
  assert.equal(response.body, '');
});

test('transcript route returns whole lines from the offset and holds back a line still being written', async () => {
  const first = '{"type":"user","n":1}\n';
  const second = '{"type":"assistant","n":2,"text":"größer"}\n';
  const files = { [FILE]: first + second + '{"type":"assis' };
  const harness = transcriptHarness(files);

  const all = await requestHttp(harness, 'GET', '/agenthub/transcript?from=0', auth);
  assert.equal(all.status, 200);
  assert.equal(all.body, first + second);
  assert.equal(all.headers['x-transcript-next'], String(Buffer.byteLength(first + second)));
  assert.equal(all.headers['x-transcript-size'], String(Buffer.byteLength(files[FILE])));

  // Offsets count bytes, so a multi-byte character before the cursor does not shift it.
  const rest = await requestHttp(harness, 'GET', '/agenthub/transcript?from=' + Buffer.byteLength(first), auth);
  assert.equal(rest.body, second);

  // The half line comes once it is finished, from the cursor the last answer gave.
  const cursor = all.headers['x-transcript-next'];
  const waiting = await requestHttp(harness, 'GET', '/agenthub/transcript?from=' + cursor, auth);
  assert.equal(waiting.body, '');
  assert.equal(waiting.headers['x-transcript-next'], cursor);
  files[FILE] += 'tant"}\n';
  const finished = await requestHttp(harness, 'GET', '/agenthub/transcript?from=' + cursor, auth);
  assert.equal(finished.body, '{"type":"assistant"}\n');
});

test('transcript route reports the size of a file shorter than the cursor so the hub starts over', async () => {
  const harness = transcriptHarness({ [FILE]: '{"n":1}\n' });
  const response = await requestHttp(harness, 'GET', '/agenthub/transcript?from=500', auth);
  assert.equal(response.status, 200);
  assert.equal(response.headers['x-transcript-size'], '8');
  assert.equal(response.body, '');
});

test('transcript route rejects an offset that is not a byte count', async () => {
  const harness = transcriptHarness({ [FILE]: '{"n":1}\n' });
  for (const from of ['-1', 'abc', '1e3']) {
    const response = await requestHttp(harness, 'GET', '/agenthub/transcript?from=' + from, auth);
    assert.equal(response.status, 400, from);
  }
});

test('upload urls are refreshed from the hub every six hours and used by the next uploads', async () => {
  const fresh = {
    statePutUrl: 'https://storage.invalid/state?sig=new',
    scrollbackPutUrl: 'https://storage.invalid/scroll?sig=new',
    transcriptPutUrl: 'https://storage.invalid/transcript?sig=new'
  };
  const harness = createHarness({
    AGENTHUB_CALLBACK_URL: 'https://backend.invalid/internal/session',
    AGENTHUB_CALLBACK_TOKEN: 'correct-token',
    AGENTHUB_SCROLLBACK_PUT_URL: 'https://storage.invalid/scroll?sig=old'
  }, {}, {
    fetchResponse: url => url.endsWith('/artifact-urls')
      ? { ok: true, json: async () => fresh, text: async () => '' }
      : undefined
  });
  const refreshes = () => harness.requests.filter(r => r.url.endsWith('/artifact-urls'));

  for (let i = 0; i < 719; i++) harness.intervals[0].callback();
  await tick();
  assert.equal(refreshes().length, 0);
  assert.ok(harness.commands.some(c => c.file === 'curl' && c.args.includes('https://storage.invalid/scroll?sig=old')));

  harness.intervals[0].callback();
  await tick();
  assert.equal(refreshes().length, 1);
  assert.equal(refreshes()[0].options.method, 'POST');
  assert.equal(refreshes()[0].options.headers['X-Agent-Token'], 'correct-token');

  harness.commands.length = 0;
  harness.intervals[0].callback();
  await tick();
  const upload = harness.commands.find(c => c.file === 'curl' && c.args.includes('/tmp/scrollback.log'));
  assert.equal(upload.args.at(-1), 'https://storage.invalid/scroll?sig=new');
});

test('a refreshed value that is not an http(s) url is not taken', async () => {
  const harness = createHarness({
    AGENTHUB_CALLBACK_URL: 'https://backend.invalid/internal/session',
    AGENTHUB_CALLBACK_TOKEN: 'correct-token',
    AGENTHUB_SCROLLBACK_PUT_URL: 'https://storage.invalid/scroll?sig=old'
  }, {}, {
    fetchResponse: url => url.endsWith('/artifact-urls')
      ? { ok: true, json: async () => ({ scrollbackPutUrl: '--output=/home/agent/.bashrc' }) }
      : undefined
  });
  for (let i = 0; i < 720; i++) harness.intervals[0].callback();
  await tick();
  harness.commands.length = 0;
  harness.intervals[0].callback();
  await tick();
  const upload = harness.commands.find(c => c.file === 'curl' && c.args.includes('/tmp/scrollback.log'));
  assert.equal(upload.args.at(-1), 'https://storage.invalid/scroll?sig=old');
});
