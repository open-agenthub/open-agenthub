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

test('read_file fetches an S3-backed file instead of reporting it missing', async () => {
  // materialize only reports a localPath for pod-backed files. An S3-backed one arrives with a
  // presigned downloadUrl and no local copy, so requiring localPath made every such file
  // unreadable — the agent answered file_not_found for a file the session listed as Ready, which
  // is the normal case whenever object storage is configured.
  const { createFilesToolHandlers } = await import('../../files/server.mjs');
  const png = Buffer.from([0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a]);
  const fetched = [];
  const client = {
    materialize: async () => [{
      id: 'b'.repeat(32), name: 'shot.png', mimeType: 'image/png', size: png.length,
      storageKind: 'S3', localPath: null, downloadUrl: 'https://storage.test/shot.png'
    }]
  };
  const fetchImpl = async url => {
    fetched.push(url);
    // Slice to the view's own range: Buffer allocations are pooled, so .buffer is usually
    // larger than the data and a naive slice(0) hands back the whole pool.
    return { ok: true, status: 200,
      arrayBuffer: async () => png.buffer.slice(png.byteOffset, png.byteOffset + png.byteLength) };
  };

  const { handlers } = createFilesToolHandlers({ client, fetch: fetchImpl });
  const result = await handlers.read_file({ fileId: 'b'.repeat(32) });

  assert.deepEqual(fetched, ['https://storage.test/shot.png']);
  assert.equal(result.content[0].type, 'image');
  assert.equal(result.content[0].data, png.toString('base64'));
});

test('read_file describes a document without needing its bytes', async () => {
  // A PDF is described, never returned, so it needs no local copy at all. The old localPath
  // guard threw before reaching this branch, which left even the metadata unavailable.
  const { createFilesToolHandlers } = await import('../../files/server.mjs');
  const client = {
    materialize: async () => [{
      id: 'c'.repeat(32), name: 'CV.pdf', mimeType: 'application/pdf', size: 181_000,
      storageKind: 'S3', localPath: null, downloadUrl: 'https://storage.test/CV.pdf'
    }]
  };
  const { handlers } = createFilesToolHandlers({
    client, fetch: async () => { throw new Error('must not download a document'); }
  });

  const result = await handlers.read_file({ fileId: 'c'.repeat(32) });

  assert.equal(result.structuredContent.readable, false);
  assert.equal(result.structuredContent.file.name, 'CV.pdf');
});

test('read_file rejects a download whose size does not match the record', async () => {
  // The record's size is what the quota was charged for and what completion verified, so a
  // mismatch means the stored object changed rather than a harmless rounding.
  const { createFilesToolHandlers } = await import('../../files/server.mjs');
  const client = {
    materialize: async () => [{
      id: 'd'.repeat(32), name: 'shot.png', mimeType: 'image/png', size: 99,
      storageKind: 'S3', localPath: null, downloadUrl: 'https://storage.test/shot.png'
    }]
  };
  const { handlers } = createFilesToolHandlers({
    client,
    fetch: async () => ({ ok: true, status: 200, arrayBuffer: async () => new ArrayBuffer(8) })
  });

  // createFilesToolHandlers returns the raw handlers; the isError wrapper lives in
  // createFilesServer, so the rejection surfaces directly here.
  await assert.rejects(
    () => handlers.read_file({ fileId: 'd'.repeat(32) }),
    /file_not_found/);
});
