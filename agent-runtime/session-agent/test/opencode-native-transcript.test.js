'use strict';
const test = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const os = require('node:os');
const path = require('node:path');

test('OpenCode records native file sources once, skips subagents and waits for completed answers', async () => {
  const { createTranscriptRecorder } = await import('../../opencode/native-transcript.mjs');
  const home = fs.mkdtempSync(path.join(os.tmpdir(), 'transcript-'));
  try {
    let subagent = false;
    const user = { info: { id: 'u1', role: 'user', time: { created: 1000 } }, parts: [
      { type: 'text', text: 'look' },
      { type: 'file', mime: 'image/png', url: 'data:image/png;base64,large', filename: 'pasted.png',
        source: { type: 'file', path: '/workspace/.agenthub/files/0123456789abcdef0123456789abcdef/pasted.png' } }
    ] };
    const answer = { info: { id: 'a1', role: 'assistant', time: { created: 2000 } }, parts: [{ type: 'text', text: 'answer' }] };
    const client = { session: { get: async () => ({ data: { parentID: subagent ? 'parent' : undefined } }),
      messages: async () => ({ data: [user, answer] }) } };
    const record = createTranscriptRecorder({ client, env: { HOME: home } });
    await record('ses_test', user);
    const file = path.join(home, '.opencode/agenthub-transcripts/ses_test.jsonl');
    let rows = fs.readFileSync(file, 'utf8').trim().split('\n').map(JSON.parse);
    assert.equal(rows.length, 1);
    assert.equal(rows[0].message.content[1].source.path, user.parts[1].source.path);
    assert.ok(!fs.readFileSync(file, 'utf8').includes('base64'));
    answer.info.time.completed = 3000;
    await Promise.all([record('ses_test'), record('ses_test')]);
    assert.equal(fs.readFileSync(file, 'utf8').trim().split('\n').length, 2);
    subagent = true;
    await record('ses_child', user);
    assert.ok(!fs.existsSync(path.join(home, '.opencode/agenthub-transcripts/ses_child.jsonl')));
  } finally { fs.rmSync(home, { recursive: true, force: true }); }
});

test('OpenCode confirms its fresh /new screen before the lazy transcript exists', () => {
  const driver = require('../../opencode/driver');
  const fresh = ['  ┃  Ask anything… "Fix broken tests"', '  tab agents  ctrl+p commands'];
  assert.equal(driver.contextClearScreen(fresh), true);
  assert.equal(driver.contextClearScreen(['  ┃ Ask anything "Fix a TODO"', fresh[1]]), true);
  assert.equal(driver.contextClearScreen([fresh[0]]), false);
  // The same box says the TUI reads keys: 1.18.35 drops what is typed before it is drawn (#306).
  // Rows of its first screen in a 120x40 PTY.
  const measured = ['                       ┃  Ask anything "Fix a TODO in the codebase"', '                       ┃',
    '                       ┃  Build   Big Pickle OpenCode Zen', '                                                                       tab agents  ctrl+p commands'];
  assert.equal(driver.inputReadyScreen(measured), true);
  assert.equal(driver.inputReadyScreen(['', '  ~                                                    1.18.35']), false);
  assert.equal(driver.contextClearScreen([' ┃ unsent draft', fresh[1]]), false);
  assert.equal(driver.contextClearScreen([' ┃ NORMAL-MATCHER-PROBE', 'NO-IMAGE', ' ┃', fresh[1]]), false);
});

test('OpenCode export bounds text and files and recovers a missing deduplication index', async () => {
  const { createTranscriptRecorder } = await import('../../opencode/native-transcript.mjs');
  const home = fs.mkdtempSync(path.join(os.tmpdir(), 'transcript-bounds-'));
  try {
    const user = { info: { id: 'u1', role: 'user', time: { created: 1000 } }, parts: [
      { type: 'text', text: 'x'.repeat(100_000) },
      ...Array.from({ length: 6 }, (_, n) => ({ type: 'file', mime: 'image/png',
        source: { path: `/workspace/.agenthub/files/${String(n).padStart(32, '0')}/pasted.png` } }))
    ] };
    let query;
    const client = { session: { get: async () => ({ data: {} }), messages: async input => {
      query = input.query; return { data: [user] };
    } } };
    await createTranscriptRecorder({ client, env: { HOME: home } })('ses_test');
    const file = path.join(home, '.opencode/agenthub-transcripts/ses_test.jsonl');
    const row = JSON.parse(fs.readFileSync(file, 'utf8').trim());
    assert.deepEqual(query, { limit: 100 });
    assert.equal(row.message.content[0].text.length, 50_000);
    assert.equal(row.message.content.filter(part => part.type === 'file').length, 5);
    fs.unlinkSync(file + '.ids.json');
    await createTranscriptRecorder({ client, env: { HOME: home } })('ses_test');
    assert.equal(fs.readFileSync(file, 'utf8').trim().split('\n').length, 1);
  } finally { fs.rmSync(home, { recursive: true, force: true }); }
});

test('recovery tail does not replace the most recent deduplication identities with older rows', async () => {
  const { createTranscriptRecorder } = await import('../../opencode/native-transcript.mjs');
  const home = fs.mkdtempSync(path.join(os.tmpdir(), 'transcript-index-'));
  try {
    const root = path.join(home, '.opencode/agenthub-transcripts');
    fs.mkdirSync(root, { recursive: true });
    const ids = Array.from({ length: 300 }, (_, n) => `msg_${n + 1}`);
    const file = path.join(root, 'ses_test.jsonl');
    fs.writeFileSync(file, ids.map(uuid => JSON.stringify({ type: 'user', uuid,
      message: { content: [{ type: 'text', text: 'short' }] } })).join('\n') + '\n');
    fs.writeFileSync(file + '.ids.json', JSON.stringify(ids.slice(-200)));
    const client = { session: { get: async () => ({ data: {} }), messages: async () => ({ data: [] }) } };
    await createTranscriptRecorder({ client, env: { HOME: home } })('ses_test');
    assert.deepEqual(JSON.parse(fs.readFileSync(file + '.ids.json', 'utf8')), ids.slice(-200));
  } finally { fs.rmSync(home, { recursive: true, force: true }); }
});

test('interrupted initial snapshot index update recovers without replaying an entire batch', async () => {
  const { createTranscriptRecorder } = await import('../../opencode/native-transcript.mjs');
  const home = fs.mkdtempSync(path.join(os.tmpdir(), 'transcript-interrupted-'));
  try {
    const messages = Array.from({ length: 20 }, (_, n) => ({
      info: { id: `msg_${n}`, role: 'user', time: { created: 1000 + n } },
      parts: [{ type: 'text', text: 'x'.repeat(50_000) }]
    }));
    const client = { session: { get: async () => ({ data: {} }), messages: async () => ({ data: messages }) } };
    let fail = true;
    const interruptedFs = { ...fs, writeFileSync(file, ...args) {
      if (fail && file.endsWith('.part')) { fail = false; throw new Error('interrupted-index'); }
      return fs.writeFileSync(file, ...args);
    } };
    await assert.rejects(createTranscriptRecorder({ client, env: { HOME: home }, fsImpl: interruptedFs })('ses_test'),
      /interrupted-index/);
    await createTranscriptRecorder({ client, env: { HOME: home } })('ses_test');
    const file = path.join(home, '.opencode/agenthub-transcripts/ses_test.jsonl');
    const rows = fs.readFileSync(file, 'utf8').trim().split('\n').map(JSON.parse);
    assert.equal(rows.length, 20);
    assert.equal(new Set(rows.map(row => row.uuid)).size, 20);
  } finally { fs.rmSync(home, { recursive: true, force: true }); }
});

test('plugin instance replacement serializes writes to the same native journal', async () => {
  const { createTranscriptRecorder } = await import('../../opencode/native-transcript.mjs');
  const home = fs.mkdtempSync(path.join(os.tmpdir(), 'transcript-reload-'));
  try {
    const user = { info: { id: 'msg_1', role: 'user', time: { created: 1000 } }, parts: [{ type: 'text', text: 'one turn' }] };
    const client = { session: { get: async () => ({ data: {} }), messages: async () => ({ data: [user] }) } };
    await Promise.all([
      createTranscriptRecorder({ client, env: { HOME: home } })('ses_test'),
      createTranscriptRecorder({ client, env: { HOME: home } })('ses_test')
    ]);
    const file = path.join(home, '.opencode/agenthub-transcripts/ses_test.jsonl');
    assert.equal(fs.readFileSync(file, 'utf8').trim().split('\n').length, 1);
  } finally { fs.rmSync(home, { recursive: true, force: true }); }
});
