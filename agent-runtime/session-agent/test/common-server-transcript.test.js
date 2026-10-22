'use strict';

// GET /agenthub/transcript, which the hub follows a running session's conversation with, and
// the refresh of the presigned upload urls (#111, docs/transcripts.md).
const assert = require('node:assert/strict');
const test = require('node:test');

const { createHarness, requestHttp, tick } = require('./common-server-harness');

const FILE = '/home/agent/.claude/projects/-workspace-repo/s.jsonl';
const auth = { 'X-Agent-Token': 'correct-token' };

test('live transcript masks file and env secrets before persistence and preserves raw byte cursors', async () => {
  const first = JSON.stringify({ text: 'file-secret-4567 and env-secret-0123 größer' }) + '\n';
  const second = JSON.stringify({ text: 'env-secret-0123 again' }) + '\n';
  const files = { [FILE]: first + second, '/secrets/API_TOKEN': 'file-secret-4567' };
  const harness = transcriptHarness(files, {
    AGENTHUB_SECRETS_DIR: '/secrets', AGENTHUB_SECRET_ENV: 'DEPLOY_TOKEN', DEPLOY_TOKEN: 'env-secret-0123'
  });
  const response = await requestHttp(harness, 'GET', '/agenthub/transcript', auth);
  assert.equal(response.body, (first + second).replaceAll('file-secret-4567', '[secret:API_TOKEN]').replaceAll('env-secret-0123', '[secret:DEPLOY_TOKEN]'));
  assert.equal(response.headers['content-length'], Buffer.byteLength(response.body));
  assert.equal(response.headers['x-transcript-next'], String(Buffer.byteLength(first + second)));
  assert.equal(response.headers['x-transcript-size'], String(Buffer.byteLength(first + second)));
  const page = await requestHttp(harness, 'GET', '/agenthub/transcript?from=' + Buffer.byteLength(first), auth);
  assert.equal(page.body, second.replaceAll('env-secret-0123', '[secret:DEPLOY_TOKEN]'));
  files['/secrets/API_TOKEN'] = 'rotated-file-secret';
  files[FILE] += JSON.stringify({ text: 'rotated-file-secret' }) + '\n';
  const rotated = await requestHttp(harness, 'GET', '/agenthub/transcript?from=' + response.headers['x-transcript-next'], auth);
  assert.equal(rotated.body, JSON.stringify({ text: '[secret:API_TOKEN]' }) + '\n');
  assert.equal(files[FILE].startsWith(first + second), true);
});

function transcriptHarness(files, environment = {}) {
  return createHarness({ AGENTHUB_CALLBACK_TOKEN: 'correct-token', ...environment },
    { findTranscript: () => (FILE in files ? FILE : null) }, { files });
}

test('mask expansion stays within the wire limit without skipping records', async () => {
  const line = JSON.stringify({ text: '12345678'.repeat(240_000) }) + '\n';
  const raw = line.repeat(4);
  const harness = transcriptHarness({ [FILE]: raw }, {
    AGENTHUB_SECRET_ENV: 'LONG_SECRET_NAME', LONG_SECRET_NAME: '12345678'
  });
  let from = 0;
  let lines = 0;
  while (from < Buffer.byteLength(raw)) {
    const page = await requestHttp(harness, 'GET', '/agenthub/transcript?from=' + from, auth);
    assert.ok(Buffer.byteLength(page.body) <= 8 * 1024 * 1024);
    assert.equal(page.body.includes('12345678'), false);
    const next = Number(page.headers['x-transcript-next']);
    assert.ok(next > from);
    lines += page.body.split('\n').length - 1;
    from = next;
  }
  assert.equal(lines, 4);
});

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
  const upload = harness.commands.find(c => c.file === 'curl' && c.args.includes('/tmp/agenthub-scrollback-proof/scrollback.log'));
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
  const upload = harness.commands.find(c => c.file === 'curl' && c.args.includes('/tmp/agenthub-scrollback-proof/scrollback.log'));
  assert.equal(upload.args.at(-1), 'https://storage.invalid/scroll?sig=old');
});

// A resumed pod's transcript comes from a state archive written at most every two minutes; the
// hub's copy is at most 30 seconds old (#146).
function resumeHarness(restored, stored, options = {}) {
  return createHarness({
    AGENTHUB_CALLBACK_TOKEN: 'correct-token',
    AGENTHUB_RESUME: '1',
    AGENTHUB_CALLBACK_URL: 'https://hub.invalid/internal/sessions/s1'
  }, { findTranscript: () => FILE }, {
    files: { [FILE]: restored },
    // The release timer is what a hub that never answers runs into; it must not fire on its own.
    setTimeout: () => 0,
    fetchResponse: url => url.endsWith('/transcript')
      ? options.transcript || { ok: true, text: async () => stored }
      : { ok: true, text: async () => '' },
    ...options.harness
  });
}

test('a resumed session appends the turns its archive missed before the agent starts', async () => {
  const harness = resumeHarness('{"n":1}\n', '{"n":1}\n{"n":2}\n');
  await tick(); await tick();

  const ask = harness.requests.find(r => r.url === 'https://hub.invalid/internal/sessions/s1/transcript');
  assert.equal(ask.options.headers['X-Agent-Token'], 'correct-token');
  assert.equal(ask.options.method, undefined);
  const written = harness.writes.find(w => w.file === FILE + '.agenthub-tmp');
  assert.equal(Buffer.from(written.data).toString(), '{"n":1}\n{"n":2}\n');
  assert.deepEqual(harness.renames.at(-1), { from: FILE + '.agenthub-tmp', to: FILE });
  assert.equal(harness.terminals.length, 1);
});

test('a resumed session keeps its archive when the hub has nothing newer or another history', async () => {
  for (const stored of ['', '{"n":1}\n', '{"other":1}\n{"other":2}\n']) {
    const harness = resumeHarness('{"n":1}\n', stored);
    await tick(); await tick();
    assert.equal(harness.writes.filter(w => w.file.startsWith(FILE)).length, 0, JSON.stringify(stored));
    assert.equal(harness.terminals.length, 1);
  }
});

test('a resumed session starts from its archive when the hub cannot be asked', async () => {
  const harness = resumeHarness('{"n":1}\n', '', { transcript: { ok: false, status: 503, text: async () => '' } });
  await tick(); await tick();
  assert.equal(harness.writes.filter(w => w.file.startsWith(FILE)).length, 0);
  assert.equal(harness.terminals.length, 1);
});

test('the restored transcript is neither served nor uploaded until it has caught up', async () => {
  let answer;
  const harness = resumeHarness('{"n":1}\n', '', {
    transcript: { ok: true, text: () => new Promise(resolve => { answer = resolve; }) },
    harness: {}
  });
  await tick(); await tick();

  // The hub would read the older file live and shrink the Workspace, or upload it over its copy.
  const early = await requestHttp(harness, 'GET', '/agenthub/transcript?from=0', auth);
  assert.equal(early.headers['x-transcript-size'], '0');
  assert.equal(harness.terminals.length, 0);

  answer('{"n":1}\n{"n":2}\n');
  await tick(); await tick();
  assert.equal(harness.terminals.length, 1);
});

test('conversation replacement changes identity even when the new file is larger and uploads its boundary', async () => {
  const next = FILE.replace('s.jsonl', 'new.jsonl');
  const files = { [FILE]: '{"old":1}\n', [next]: '{"new":"a larger conversation"}\n' };
  let active = FILE;
  const harness = createHarness({ AGENTHUB_CALLBACK_TOKEN: 'correct-token',
    AGENTHUB_CALLBACK_URL: 'https://example.com/internal/sessions/s1' },
  { findTranscript: () => active }, { files });
  const first = await requestHttp(harness, 'GET', '/agenthub/transcript?from=0', auth);
  active = next;
  const changed = await requestHttp(harness, 'GET', '/agenthub/transcript?from=' + first.headers['x-transcript-next'], auth);
  assert.notEqual(changed.headers['x-transcript-id'], first.headers['x-transcript-id']);
  const fresh = await requestHttp(harness, 'GET', '/agenthub/transcript?from=0', auth);
  assert.match(fresh.body, /Conversation cleared/);
  assert.match(fresh.body, /a larger conversation/);
  assert.equal(fresh.body.includes('"old"'), true);
  assert.ok(fresh.body.indexOf('"old"') < fresh.body.indexOf('Conversation cleared'));
  harness.intervals[0].callback();
  await tick(); await tick();
  const upload = harness.requests.find(r => r.options.method === 'PUT' && r.url.endsWith('/transcript'));
  assert.match(upload.options.body, /Conversation cleared/);
  assert.match(upload.options.body, /a larger conversation/);
});

test('resume catch-up restores only the fresh context from the owner history view', async () => {
  const restored = '{"new":1}\n';
  const stored = '{"old":1}\n{"type":"agenthub_conversation","id":"new","text":"Conversation cleared"}\n' + restored + '{"new":2}\n';
  const harness = resumeHarness(restored, stored);
  await tick(); await tick();
  const written = harness.writes.find(w => w.file === FILE + '.agenthub-tmp');
  assert.equal(Buffer.from(written.data).toString(), restored + '{"new":2}\n');
});
