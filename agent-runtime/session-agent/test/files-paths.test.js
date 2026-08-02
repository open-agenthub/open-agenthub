const assert = require('node:assert/strict');
const fs = require('node:fs');
const os = require('node:os');
const path = require('node:path');
const test = require('node:test');

test('upload source must remain below workspace or managed output', async t => {
  const { allowedSource } = await import('../../files/paths.mjs');
  const root = await fs.promises.mkdtemp(path.join(os.tmpdir(), 'agenthub-paths-'));
  t.after(() => fs.promises.rm(root, { recursive: true, force: true }));
  const workspace = path.join(root, 'workspace');
  const managed = path.join(root, 'managed');
  const outside = path.join(root, 'secret.txt');
  await fs.promises.mkdir(path.join(workspace, 'out'), { recursive: true });
  await fs.promises.mkdir(managed);
  await fs.promises.writeFile(path.join(workspace, 'out', 'report.pdf'), 'pdf');
  await fs.promises.writeFile(path.join(managed, 'image.png'), 'png');
  await fs.promises.writeFile(outside, 'secret');

  assert.equal(await allowedSource(path.join(workspace, 'out', 'report.pdf'), { workspace, managedRoot: managed }),
    await fs.promises.realpath(path.join(workspace, 'out', 'report.pdf')));
  assert.equal(await allowedSource(path.join(managed, 'image.png'), { workspace, managedRoot: managed }),
    await fs.promises.realpath(path.join(managed, 'image.png')));
  await assert.rejects(() => allowedSource(outside, { workspace, managedRoot: managed }), /file_source_not_allowed/);
});

test('upload source rejects a symlink that resolves outside an allowed root', async t => {
  const { allowedSource } = await import('../../files/paths.mjs');
  const root = await fs.promises.mkdtemp(path.join(os.tmpdir(), 'agenthub-paths-link-'));
  t.after(() => fs.promises.rm(root, { recursive: true, force: true }));
  const workspace = path.join(root, 'workspace');
  const managed = path.join(root, 'managed');
  const outside = path.join(root, 'outside');
  await fs.promises.mkdir(workspace);
  await fs.promises.mkdir(managed);
  await fs.promises.mkdir(outside);
  await fs.promises.writeFile(path.join(outside, 'secret.txt'), 'secret');
  await fs.promises.symlink(outside, path.join(workspace, 'link'), 'junction');

  await assert.rejects(() => allowedSource(path.join(workspace, 'link', 'secret.txt'),
    { workspace, managedRoot: managed }), /file_source_not_allowed/);
});
