'use strict';

const assert = require('node:assert/strict');
const fs = require('node:fs');
const os = require('node:os');
const path = require('node:path');
const { Readable } = require('node:stream');
const test = require('node:test');

const { LocalFileStore } = require('../../files/local-store');

const FILE_ID = 'a'.repeat(32);

async function temporaryStore() {
  const directory = await fs.promises.mkdtemp(path.join(os.tmpdir(), 'agenthub-files-'));
  return {
    directory,
    store: new LocalFileStore({ root: path.join(directory, 'managed') }),
    async dispose() { await fs.promises.rm(directory, { recursive: true, force: true }); }
  };
}

test('local store rejects invalid ids and an escaping symlink', async t => {
  const harness = await temporaryStore();
  t.after(() => harness.dispose());
  await assert.rejects(() => harness.store.open('../secret'), /invalid_file_id/);

  await fs.promises.mkdir(path.join(harness.directory, 'managed'), { recursive: true });
  const outside = path.join(harness.directory, 'outside');
  await fs.promises.mkdir(outside);
  try {
    await fs.promises.symlink(outside, path.join(harness.directory, 'managed', FILE_ID),
      process.platform === 'win32' ? 'junction' : 'dir');
  } catch (error) {
    if (error.code === 'EPERM') return t.skip('symlink creation is unavailable');
    throw error;
  }

  await assert.rejects(() => harness.store.open(FILE_ID), /managed_root_escape/);
});

test('put removes partial content when the stream exceeds its ceiling', async t => {
  const harness = await temporaryStore();
  t.after(() => harness.dispose());

  await assert.rejects(
    () => harness.store.put(FILE_ID, 'shot.png', Readable.from(['12345']), 4),
    /file_too_large/);

  assert.equal(await harness.store.head(FILE_ID), null);
  const entries = await fs.promises.readdir(path.join(harness.directory, 'managed', FILE_ID));
  assert.deepEqual(entries, []);
});

test('put atomically round-trips a normalized display name and supports removal', async t => {
  const harness = await temporaryStore();
  t.after(() => harness.dispose());

  const stored = await harness.store.put(
    FILE_ID, '../shot.png', Readable.from([Buffer.from('image')]), 5);
  assert.equal(stored.name, 'shot.png');
  assert.equal(stored.size, 5);

  const metadata = await harness.store.head(FILE_ID);
  assert.deepEqual({ name: metadata.name, size: metadata.size }, { name: 'shot.png', size: 5 });
  const readable = await harness.store.open(FILE_ID);
  const chunks = [];
  for await (const chunk of readable.stream) chunks.push(chunk);
  assert.equal(Buffer.concat(chunks).toString(), 'image');

  await harness.store.remove(FILE_ID);
  assert.equal(await harness.store.head(FILE_ID), null);
});
