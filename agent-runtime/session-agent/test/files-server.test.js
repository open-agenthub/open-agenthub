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
  // The listing is metadata only. Without naming the next step the agent sees ids and sizes and no
  // way to get the bytes, which is how a session ended up reporting it could only see file names.
  assert.match(listed.structuredContent.hint, /read_file/);
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
test('read_file returns inline content and a path for both an image and oversized text', async t => {
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
  // Inline bytes are not enough on their own: asked to put the image in its working directory the
  // agent needs somewhere to copy it from, and base64 in the transcript is not that.
  assert.equal(image.structuredContent.localPath, imagePath);

  // Text over the inline limit is reported by path rather than refused. It used to raise
  // file_text_too_large with no path at all, which left a file the agent could have grepped
  // unreadable through this tool.
  const text = await handlers.read_file({ fileId: records.text.id });
  assert.equal(text.structuredContent.localPath, textPath);
  assert.equal(text.structuredContent.readable, true);
  assert.ok(!text.content.some(block => block.text === 'hello'), 'oversized text must not be inlined');
});

test('read_file reports a path for text that is not valid UTF-8', async t => {
  // A .txt whose bytes are not UTF-8 used to fail the whole call with an opaque code, even though
  // the file was already on disk and the agent could have chosen an encoding itself.
  const { createFilesToolHandlers } = await import('../../files/server.mjs');
  const root = await fs.promises.mkdtemp(path.join(os.tmpdir(), 'agenthub-files-latin1-'));
  t.after(() => fs.promises.rm(root, { recursive: true, force: true }));
  const textPath = path.join(root, 'notes.txt');
  const bytes = Buffer.from([0xff, 0xfe, 0x41]);
  await fs.promises.writeFile(textPath, bytes);
  const record = { id: 'c'.repeat(32), name: 'notes.txt', mimeType: 'text/plain', size: 3, localPath: textPath };
  const { handlers } = createFilesToolHandlers({ client: { materialize: async () => [record] } });

  const result = await handlers.read_file({ fileId: record.id });

  assert.equal(result.structuredContent.localPath, textPath);
  assert.equal(result.structuredContent.readable, true);
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

async function harness(t, record, bytes, options = {}) {
  const { createFilesToolHandlers } = await import('../../files/server.mjs');
  const root = await fs.promises.mkdtemp(path.join(os.tmpdir(), 'agenthub-files-read-'));
  t.after(() => fs.promises.rm(root, { recursive: true, force: true }));
  const downloads = [];
  const client = {
    materialize: async () => [record],
    contentStream: async fileId => { downloads.push(fileId); return webStream(bytes); }
  };
  const { handlers } = createFilesToolHandlers({ ...options, client, managedRoot: root });
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
  // The fetched copy stays on disk and is reported, so the agent can also move the image into its
  // own working directory instead of only looking at it.
  assert.equal(await fs.promises.readFile(result.structuredContent.localPath).then(b => b.toString('base64')),
    png.toString('base64'));
});

test('read_file reports an oversized image by path instead of refusing it', async t => {
  // Over the inline ceiling the bytes cannot go in the reply, but the file is already on disk by
  // then. Erroring here threw that away and left the agent with nothing.
  const png = Buffer.alloc(32, 7);
  const { handlers } = await harness(t, s3Record({ size: png.length }), png, { maxImageBytes: 8 });

  const result = await handlers.read_file({ fileId: 'b'.repeat(32) });

  assert.ok(result.structuredContent.localPath, 'expected a localPath');
  assert.ok(!result.content.some(block => block.type === 'image'), 'image must not be inlined');
  assert.equal((await fs.promises.readFile(result.structuredContent.localPath)).length, png.length);
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

async function registeredTools(t, client, options = {}) {
  const { createFilesServer } = await import('../../files/server.mjs');
  const root = await fs.promises.mkdtemp(path.join(os.tmpdir(), 'agenthub-files-server-'));
  t.after(() => fs.promises.rm(root, { recursive: true, force: true }));
  return createFilesServer({ ...options, client, managedRoot: root })._registeredTools;
}

test('an oversized download surfaces file_too_large, not a generic failure', async t => {
  // LocalFileStore stops a download that runs past the size the record was completed with. That
  // code was missing from the stable allowlist, so it reached the agent as files_operation_failed —
  // nothing it could act on, for the one condition worth retrying.
  const client = {
    materialize: async () => [s3Record({ name: 'CV.pdf', mimeType: 'application/pdf', size: 4 })],
    contentStream: async () => webStream(Buffer.alloc(64))
  };
  const tools = await registeredTools(t, client);

  const result = await tools.read_file.handler({ fileId: 'b'.repeat(32) }, {});

  assert.equal(result.isError, true);
  assert.equal(JSON.parse(result.content[0].text).error, 'file_too_large');
});

test('read_file advertises that it returns a path', async t => {
  // The description is the only thing the model reads when deciding whether this tool can do what
  // it wants. While it said "or return safe document metadata", a model asked to put a file on
  // disk had no reason to think read_file could help.
  const tools = await registeredTools(t, { list: async () => [] });

  assert.match(tools.read_file.description, /path/);
  assert.match(tools.list_files.description, /read_file/);
});
