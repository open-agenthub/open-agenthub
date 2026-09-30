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

// The four tests below cover reading a file that lives in object storage. They use the real
// LocalFileStore against a temp directory rather than a fake, because the point is that the file
// ends up on disk where the agent can grep it.

function s3Record(overrides = {}) {
  return {
    id: 'b'.repeat(32), name: 'shot.png', mimeType: 'image/png', size: 8,
    storageKind: 'S3', localPath: null,
    // A presigned url is offered and must be ignored: it expires after PresignMinutes, so a read
    // later than the listing failed with a signature error the agent could only report as a
    // missing file.
    downloadUrl: 'https://storage.test/expires-soon',
    ...overrides
  };
}

function webStream(buffer) {
  return new ReadableStream({
    start(controller) { controller.enqueue(new Uint8Array(buffer)); controller.close(); }
  });
}

async function harness(t, record, bytes) {
  const { createFilesToolHandlers } = await import('../../files/server.mjs');
  const root = await fs.promises.mkdtemp(path.join(os.tmpdir(), 'agenthub-files-read-'));
  t.after(() => fs.promises.rm(root, { recursive: true, force: true }));
  const downloads = [];
  const client = {
    materialize: async () => [record],
    contentStream: async fileId => { downloads.push(fileId); return webStream(bytes); }
  };
  const { handlers } = createFilesToolHandlers({ client, managedRoot: root });
  return { handlers, downloads, root };
}

test('read_file fetches an S3-backed image through the API', async t => {
  // materialize reports a localPath only for pod-backed files, so requiring one made every
  // S3-backed file unreadable — the agent answered file_not_found for a file its own session
  // listed as Ready, which is the normal case whenever object storage is configured.
  const png = Buffer.from([0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a]);
  const { handlers, downloads } = await harness(t, s3Record({ size: png.length }), png);

  const result = await handlers.read_file({ fileId: 'b'.repeat(32) });

  assert.deepEqual(downloads, ['b'.repeat(32)]);
  assert.equal(result.content[0].type, 'image');
  assert.equal(result.content[0].data, png.toString('base64'));
});

test('read_file puts a document on disk and reports its path, not its bytes', async t => {
  // Returning the contents would fill the model's context with something it should read
  // selectively. With a path it can grep the file, read a range, or hand it to a tool. Before
  // this, a PDF answered readable:false with no path at all — a file the user had just uploaded
  // was unusable.
  const pdf = Buffer.from('%PDF-1.7\nbody');
  const record = s3Record({ name: 'CV.pdf', mimeType: 'application/pdf', size: pdf.length });
  const { handlers } = await harness(t, record, pdf);

  const result = await handlers.read_file({ fileId: 'b'.repeat(32) });

  assert.equal(result.structuredContent.readable, true);
  assert.ok(result.structuredContent.localPath, 'expected a localPath');
  assert.equal(await fs.promises.readFile(result.structuredContent.localPath, 'utf8'), pdf.toString());
  // The reply must stay small: metadata only, no base64 payload.
  assert.ok(result.content[0].text.length < 500, 'reply should not carry the file contents');
});

test('read_file reuses a copy already on disk', async t => {
  // Re-downloading on every read would make grepping a large document repeatedly expensive.
  const pdf = Buffer.from('%PDF-1.7\nbody');
  const record = s3Record({ name: 'CV.pdf', mimeType: 'application/pdf', size: pdf.length });
  const { handlers, downloads } = await harness(t, record, pdf);

  await handlers.read_file({ fileId: 'b'.repeat(32) });
  await handlers.read_file({ fileId: 'b'.repeat(32) });

  assert.deepEqual(downloads, ['b'.repeat(32)], 'expected exactly one download');
});

test('read_file rejects content whose size does not match the record', async t => {
  // The record's size is what the quota was charged for and what completion verified, so a
  // mismatch means the stored object changed rather than a harmless rounding.
  const { handlers } = await harness(t, s3Record({ size: 99 }), Buffer.alloc(8));

  // createFilesToolHandlers returns the raw handlers; the isError wrapper lives in
  // createFilesServer, so the rejection surfaces directly here.
  await assert.rejects(() => handlers.read_file({ fileId: 'b'.repeat(32) }), /file/);
});
