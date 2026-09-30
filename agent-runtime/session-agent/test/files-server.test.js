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
  const asked = [];
  const client = {
    materialize: async () => [{
      id: 'b'.repeat(32), name: 'shot.png', mimeType: 'image/png', size: png.length,
      // A presigned url is offered but must be ignored: it expires, so a read later than the
      // listing failed with a signature error the agent could only report as a missing file.
      storageKind: 'S3', localPath: null, downloadUrl: 'https://storage.test/expires-soon'
    }],
    content: async (fileId, maxBytes) => { asked.push([fileId, maxBytes]); return png; }
  };

  const { handlers } = createFilesToolHandlers({ client, maxImageBytes: 4096 });
  const result = await handlers.read_file({ fileId: 'b'.repeat(32) });

  assert.deepEqual(asked, [['b'.repeat(32), 4096]]);
  assert.equal(result.content[0].type, 'image');
  assert.equal(result.content[0].data, png.toString('base64'));
});

test('read_file describes an unreadable type without needing its bytes', async () => {
  // An Office document cannot be handed to the model as-is, so it is described. That needs no
  // bytes at all, which is why the guard stops at "does the record exist" rather than "is it on
  // disk" — the old localPath check threw first and left even the metadata unavailable.
  const { createFilesToolHandlers } = await import('../../files/server.mjs');
  const docx = 'application/vnd.openxmlformats-officedocument.wordprocessingml.document';
  const client = {
    materialize: async () => [{
      id: 'c'.repeat(32), name: 'offer.docx', mimeType: docx, size: 181_000,
      storageKind: 'S3', localPath: null, downloadUrl: 'https://storage.test/offer.docx'
    }],
    content: async () => { throw new Error('must not download an undisplayable document'); }
  };
  const { handlers } = createFilesToolHandlers({ client });

  const result = await handlers.read_file({ fileId: 'c'.repeat(32) });

  assert.equal(result.structuredContent.readable, false);
  assert.equal(result.structuredContent.reason, 'unsupported_file_type');
  assert.equal(result.structuredContent.file.name, 'offer.docx');
});

test('read_file rejects a download whose size does not match the record', async () => {
  // The record's size is what the quota was charged for and what completion verified, so a
  // mismatch means the stored object changed rather than a harmless rounding.
  const { createFilesToolHandlers } = await import('../../files/server.mjs');
  const client = {
    materialize: async () => [{
      id: 'd'.repeat(32), name: 'shot.png', mimeType: 'image/png', size: 99,
      storageKind: 'S3', localPath: null, downloadUrl: 'https://storage.test/shot.png'
    }],
    content: async () => Buffer.alloc(8)
  };
  const { handlers } = createFilesToolHandlers({ client });

  // createFilesToolHandlers returns the raw handlers; the isError wrapper lives in
  // createFilesServer, so the rejection surfaces directly here.
  await assert.rejects(
    () => handlers.read_file({ fileId: 'd'.repeat(32) }),
    /file_not_found/);
});

test('read_file returns a PDF the model can actually read', async () => {
  // Answering with metadata and readable:false made a file the user had just uploaded useless to
  // the agent: it could see the name and size and nothing else. Models read PDFs directly, so the
  // bytes go back as an embedded resource.
  const { createFilesToolHandlers } = await import('../../files/server.mjs');
  const pdf = Buffer.from('%PDF-1.7\nbody');
  const client = {
    materialize: async () => [{
      id: 'e'.repeat(32), sessionId: 's1', name: 'CV.pdf', mimeType: 'application/pdf',
      size: pdf.length, storageKind: 'S3', localPath: null
    }],
    content: async () => pdf
  };
  const { handlers } = createFilesToolHandlers({ client });

  const result = await handlers.read_file({ fileId: 'e'.repeat(32) });

  assert.equal(result.content[0].type, 'resource');
  assert.equal(result.content[0].resource.mimeType, 'application/pdf');
  assert.equal(result.content[0].resource.blob, pdf.toString('base64'));
});

test('read_file describes a PDF too large to put in the reply', async () => {
  // The blob is base64-encoded into the response, so an unbounded document would land in the
  // model's context whole. Past the limit it is described instead, with the reason named.
  const { createFilesToolHandlers } = await import('../../files/server.mjs');
  const client = {
    materialize: async () => [{
      id: 'f'.repeat(32), name: 'huge.pdf', mimeType: 'application/pdf',
      size: 40 * 1024 * 1024, storageKind: 'S3', localPath: null
    }],
    content: async () => { throw new Error('must not download an oversized document'); }
  };
  const { handlers } = createFilesToolHandlers({ client, maxDocumentBytes: 1024 });

  const result = await handlers.read_file({ fileId: 'f'.repeat(32) });

  assert.equal(result.structuredContent.readable, false);
  assert.equal(result.structuredContent.reason, 'file_too_large_to_read');
});
