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
  const fresh = ['   █▀▀█ █▀▀█ █▀▀█ █▀▀▄ █▀▀▀ █▀▀█ █▀▀█ █▀▀█', '  ┃  Ask anything… "Fix broken tests"'];
  assert.equal(driver.contextClearScreen(fresh), true);
  assert.equal(driver.contextClearScreen([' ┃  Ask anything… "Fix broken tests"']), false);
  assert.equal(driver.contextClearScreen([fresh[0], ' ┃ unsent draft']), false);
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
