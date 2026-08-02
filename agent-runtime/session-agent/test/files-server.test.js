const assert = require('node:assert/strict');
const fs = require('node:fs');
const os = require('node:os');
const path = require('node:path');
const test = require('node:test');

test('files tools expose exact strict schemas and safe metadata results', async () => {
  const { createFilesToolHandlers } = await import('../../files/server.mjs');
  const calls = [];
  const client = {
    capabilities: async () => ({ storageMode: 'temporary-pod', uploadAvailable: true }),
    list: async () => [{ id: 'a'.repeat(32), name: 'shot.png', mimeType: 'image/png', size: 4 }],
    present: async fileId => { calls.push(['present', fileId]); return { fileId, revision: 2 }; },
    dismiss: async () => { calls.push(['dismiss']); return { fileId: null, revision: 3 }; }
  };
  const { handlers, schemas } = createFilesToolHandlers({ client });

  assert.deepEqual(Object.keys(handlers).sort(), [
    'dismiss_presentation', 'list_display_capabilities', 'list_files', 'present_file', 'read_file', 'upload_file'
  ]);
  assert.equal(schemas.upload_file.safeParse({ path: '/workspace/a', extra: true }).success, false);
  const listed = await handlers.list_files({});
  assert.deepEqual(listed.structuredContent.files[0].name, 'shot.png');
  assert.equal(listed.content[0].text, JSON.stringify(listed.structuredContent));
  await handlers.present_file({ fileId: 'a'.repeat(32) });
  await handlers.dismiss_presentation({});
  assert.deepEqual(calls, [['present', 'a'.repeat(32)], ['dismiss']]);
});

test('upload_file returns JSON text and structured metadata', async t => {
  const { createFilesToolHandlers } = await import('../../files/server.mjs');
  const root = await fs.promises.mkdtemp(path.join(os.tmpdir(), 'agenthub-files-upload-'));
  t.after(() => fs.promises.rm(root, { recursive: true, force: true }));
  const workspace = path.join(root, 'workspace');
  const managedRoot = path.join(root, 'managed');
  await fs.promises.mkdir(workspace);
  const source = path.join(workspace, 'report.pdf');
  await fs.promises.writeFile(source, 'pdf');
  const client = {
    reserve: async value => ({
      file: { id: 'c'.repeat(32), name: value.name },
      upload: { kind: 'proxy', url: '/upload', headers: {} }
    }),
    upload: async () => {},
    complete: async id => ({ id, name: 'report.pdf', mimeType: 'application/pdf', size: 3 })
  };
  const { handlers } = createFilesToolHandlers({ client, workspace, managedRoot });

  const result = await handlers.upload_file({ path: source });

  assert.equal(result.structuredContent.id, 'c'.repeat(32));
  assert.equal(result.content[0].text, JSON.stringify(result.structuredContent));
});
test('read_file returns image content and bounds decoded text', async t => {
  const { createFilesToolHandlers } = await import('../../files/server.mjs');
  const root = await fs.promises.mkdtemp(path.join(os.tmpdir(), 'agenthub-files-tools-'));
  t.after(() => fs.promises.rm(root, { recursive: true, force: true }));
  const imagePath = path.join(root, 'shot.png');
  const textPath = path.join(root, 'notes.md');
  await fs.promises.writeFile(imagePath, Buffer.from([1, 2, 3]));
  await fs.promises.writeFile(textPath, 'hello');
  const records = {
    image: { id: 'a'.repeat(32), name: 'shot.png', mimeType: 'image/png', size: 3, localPath: imagePath },
    text: { id: 'b'.repeat(32), name: 'notes.md', mimeType: 'text/markdown', size: 5, localPath: textPath }
  };
  const client = { materialize: async ids => ids.map(id => id === records.image.id ? records.image : records.text) };
  const { handlers } = createFilesToolHandlers({ client, maxTextBytes: 4 });

  const image = await handlers.read_file({ fileId: records.image.id });
  assert.deepEqual(image.content[0], { type: 'image', data: 'AQID', mimeType: 'image/png' });
  await assert.rejects(() => handlers.read_file({ fileId: records.text.id }), /file_text_too_large/);
});
