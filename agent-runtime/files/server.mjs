import crypto from 'node:crypto';
import fs from 'node:fs';
import path from 'node:path';
import { pipeline } from 'node:stream/promises';
import { createRequire } from 'node:module';
import { fileURLToPath, pathToFileURL } from 'node:url';
import { FilesBackendClient } from './client.mjs';
import { allowedSource, safeDisplayName } from './paths.mjs';

const runtimeRoot = process.env.RUNTIME || fileURLToPath(new URL('../session-agent/', import.meta.url));
const runtimeRequire = createRequire(path.join(runtimeRoot, 'package.json'));
const { McpServer } = runtimeRequire('@modelcontextprotocol/sdk/server/mcp.js');
const { StdioServerTransport } = runtimeRequire('@modelcontextprotocol/sdk/server/stdio.js');
const { z } = runtimeRequire('zod');

const id = z.string().regex(/^[a-f0-9]{32}$/);
const empty = z.object({}).strict();
const upload = z.object({
  path: z.string().min(1).max(4096),
  displayName: z.string().min(1).max(255).optional()
}).strict();
const present = z.object({
  fileId: id.optional(),
  path: z.string().min(1).max(4096).optional(),
  displayName: z.string().min(1).max(255).optional()
}).strict().superRefine((value, context) => {
  if ((value.fileId ? 1 : 0) + (value.path ? 1 : 0) !== 1)
    context.addIssue({ code: z.ZodIssueCode.custom, message: 'exactly_one_file_source_required' });
  if (value.displayName && !value.path)
    context.addIssue({ code: z.ZodIssueCode.custom, message: 'display_name_requires_path' });
});

const MIME = new Map([
  ['.png', 'image/png'], ['.jpg', 'image/jpeg'], ['.jpeg', 'image/jpeg'],
  ['.webp', 'image/webp'], ['.gif', 'image/gif'], ['.pdf', 'application/pdf'],
  ['.md', 'text/markdown'], ['.markdown', 'text/markdown'], ['.txt', 'text/plain'],
  ['.docx', 'application/vnd.openxmlformats-officedocument.wordprocessingml.document'],
  ['.pptx', 'application/vnd.openxmlformats-officedocument.presentationml.presentation'],
  ['.xlsx', 'application/vnd.openxmlformats-officedocument.spreadsheetml.sheet']
]);
const IMAGE = new Set(['image/png', 'image/jpeg', 'image/webp', 'image/gif']);
const TEXT = new Set(['text/plain', 'text/markdown']);
// Returned as an embedded resource: models read these directly, so describing them instead
// leaves the agent unable to use a file the user just uploaded.
const DOCUMENT = new Set(['application/pdf']);

function metadata(value) {
  const structuredContent = value;
  return { content: [{ type: 'text', text: JSON.stringify(structuredContent) }], structuredContent };
}

export function createFilesToolHandlers(options = {}) {
  const client = options.client ?? new FilesBackendClient();
  const workspace = options.workspace ?? process.env.AGENTHUB_WORKDIR ?? '/workspace';
  const managedRoot = options.managedRoot ?? process.env.AGENTHUB_FILE_ROOT ?? '/workspace/.agenthub/files';
  const maxTextBytes = options.maxTextBytes ?? 1024 * 1024;
  const maxImageBytes = options.maxImageBytes ?? 20 * 1024 * 1024;
  // A document is base64-encoded into the reply, so this bounds what lands in the model's
  // context, not just what is transferred. Well below the 50 MB a session file may be.
  const maxDocumentBytes = options.maxDocumentBytes ?? 10 * 1024 * 1024;
  const schemas = {
    list_display_capabilities: empty, list_files: empty,
    read_file: z.object({ fileId: id }).strict(), upload_file: upload,
    present_file: present, dismiss_presentation: empty
  };

  async function uploadPath(input) {
    await fs.promises.mkdir(managedRoot, { recursive: true, mode: 0o700 });
    const source = await allowedSource(input.path, { workspace, managedRoot });
    const name = safeDisplayName(input.displayName ?? path.basename(source));
    const mimeType = MIME.get(path.extname(name).toLowerCase());
    if (!mimeType) throw new Error('unsupported_file_type');
    const staging = path.join(managedRoot, '.outgoing');
    await fs.promises.mkdir(staging, { recursive: true, mode: 0o700 });
    const staged = path.join(staging, `${crypto.randomUUID()}-${name}`);
    const noFollow = fs.constants.O_NOFOLLOW ?? 0;
    const sourceHandle = await fs.promises.open(source, fs.constants.O_RDONLY | noFollow);
    let stat;
    try {
      stat = await sourceHandle.stat();
      if (!stat.isFile()) throw new Error('file_source_not_allowed');
      const output = fs.createWriteStream(staged, { flags: 'wx', mode: 0o600 });
      try {
        await pipeline(sourceHandle.createReadStream({ autoClose: false }), output);
      } catch (error) {
        await fs.promises.rm(staged, { force: true });
        throw error;
      }
    } finally {
      await sourceHandle.close();
    }
    try {
      const reserved = await client.reserve({ name, mimeType, size: stat.size });
      await client.upload(reserved.upload, fs.createReadStream(staged), mimeType);
      return await client.complete(reserved.file.id);
    } finally {
      await fs.promises.rm(staged, { force: true });
    }
  }

  /**
   * Returns a file's bytes.
   *
   * materialize reports a localPath only for pod-backed files; an S3-backed one has no local
   * copy, and requiring localPath made every such file unreadable — the agent reported
   * file_not_found for a file its own session listed as Ready.
   *
   * The bytes come through the API rather than from the presigned url materialize also returns.
   * That url expires after PresignMinutes, so a read that happened any later than the listing
   * failed with a signature error — which the agent could only report as a missing file, and
   * plausibly blamed on the file having expired. Going through the API has no deadline and keeps
   * the storage credential out of the agent's reach.
   */
  async function readBytes(file, maxBytes) {
    if (file.localPath) return fs.promises.readFile(file.localPath);
    const data = await client.content(file.id, maxBytes);
    // The record's size is what the quota was charged for and what completion verified, so a
    // mismatch means the stored object changed underneath us rather than a harmless rounding.
    if (data.length !== file.size) throw new Error('file_not_found');
    return data;
  }

  const handlers = {
    list_display_capabilities: async () => metadata(await client.capabilities()),
    list_files: async () => metadata({ files: await client.list() }),
    read_file: async ({ fileId }) => {
      const [file] = await client.materialize([fileId]);
      if (!file) throw new Error('file_not_found');
      if (IMAGE.has(file.mimeType)) {
        // Images get their own ceiling: they are returned base64-encoded rather than as text, so
        // the text limit has nothing to do with them.
        const data = await readBytes(file, maxImageBytes);
        return { content: [{ type: 'image', data: data.toString('base64'), mimeType: file.mimeType }] };
      }
      if (TEXT.has(file.mimeType)) {
        if (file.size > maxTextBytes) throw new Error('file_text_too_large');
        const data = await readBytes(file, maxTextBytes);
        if (data.length > maxTextBytes) throw new Error('file_text_too_large');
        return { content: [{ type: 'text', text: new TextDecoder('utf-8', { fatal: true }).decode(data) }] };
      }
      if (DOCUMENT.has(file.mimeType) && file.size <= maxDocumentBytes) {
        // Handed back as an embedded resource rather than described. Models read PDFs directly,
        // so answering with metadata and readable:false made a file the user had just uploaded
        // useless to the agent — it could see the name and nothing else.
        const data = await readBytes(file, maxDocumentBytes);
        return {
          content: [{
            type: 'resource',
            resource: {
              uri: `agenthub://sessions/${file.sessionId ?? 'session'}/files/${file.id}`,
              mimeType: file.mimeType,
              blob: data.toString('base64')
            }
          }]
        };
      }
      // Anything left over — an Office document, or a PDF past the limit — is described. This
      // needs no bytes at all, so it must not depend on the file being local: that is why the
      // check above stops at "does the record exist" rather than "is it on disk".
      return metadata({
        file: { id: file.id, name: file.name, mimeType: file.mimeType, size: file.size },
        readable: false,
        reason: DOCUMENT.has(file.mimeType) ? 'file_too_large_to_read' : 'unsupported_file_type'
      });
    },
    upload_file: async input => metadata(await uploadPath(input)),
    present_file: async input => {
      const file = input.path ? await uploadPath(input) : { id: input.fileId };
      return metadata(await client.present(file.id));
    },
    dismiss_presentation: async () => metadata(await client.dismiss())
  };
  return { handlers, schemas };
}

export function createFilesServer(options = {}) {
  const server = new McpServer({ name: 'agenthub_files', version: '1.0.0' });
  const { handlers, schemas } = createFilesToolHandlers(options);
  const descriptions = {
    list_display_capabilities: 'Report which file types and previews can be displayed.',
    list_files: 'List ready files in this session.',
    read_file: 'Read an image or bounded text file, or return safe document metadata.',
    upload_file: 'Upload a file from the workspace or managed output directory.',
    present_file: 'Present an existing session file or upload and present a local file.',
    dismiss_presentation: 'Dismiss the shared file presentation.'
  };
  for (const name of Object.keys(handlers)) {
    server.registerTool(name, { description: descriptions[name], inputSchema: schemas[name] }, async input => {
      try { return await handlers[name](input ?? {}); }
      catch (error) {
        const code = safeError(error);
        return { content: [{ type: 'text', text: JSON.stringify({ error: code }) }], isError: true };
      }
    });
  }
  return server;
}

function safeError(error) {
  const message = error instanceof Error ? error.message : '';
  const stable = ['files_backend_not_configured', 'files_backend_invalid_url', 'files_backend_response_too_large',
    'files_backend_invalid_json', 'files_upload_descriptor_invalid', 'file_source_not_allowed', 'invalid_file_name',
    'unsupported_file_type', 'file_not_found', 'file_text_too_large'];
  return stable.find(code => message.includes(code)) ?? (/files_backend_http_\d{3}/.exec(message)?.[0]) ?? 'files_operation_failed';
}

if (process.argv[1] && import.meta.url === pathToFileURL(process.argv[1]).href)
  await createFilesServer().connect(new StdioServerTransport());
