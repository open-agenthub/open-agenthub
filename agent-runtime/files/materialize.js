'use strict';

const { Readable } = require('node:stream');
const { LocalFileStore } = require('./local-store');

const FILE_ID = /^[a-f0-9]{32}$/;
const MAX_METADATA_BYTES = 1_000_000;
const DEFAULT_MAX_TOTAL_BYTES = 50 * 1024 * 1024;
const MAX_ATTACHMENTS = 5;

class AttachmentMaterializer {
  constructor(options = {}) {
    const env = options.env || process.env;
    const callbackUrl = env.AGENTHUB_CALLBACK_URL;
    const token = env.AGENTHUB_CALLBACK_TOKEN;
    const sessionId = env.AGENTHUB_SESSION_ID;
    if (!callbackUrl || !token || !sessionId) throw new Error('attachment_backend_not_configured');
    let parsed;
    try { parsed = new URL(callbackUrl); } catch { throw new Error('attachment_backend_invalid_url'); }
    if (!['http:', 'https:'].includes(parsed.protocol)) throw new Error('attachment_backend_invalid_url');
    this.baseUrl = parsed.toString().replace(/\/$/, '');
    this.token = token;
    this.sessionId = sessionId;
    this.fetch = options.fetch || globalThis.fetch;
    this.maxTotalBytes = options.maxTotalBytes || DEFAULT_MAX_TOTAL_BYTES;
    this.store = options.store || new LocalFileStore({
      root: options.managedRoot || env.AGENTHUB_FILE_ROOT || '/workspace/.agenthub/files'
    });
  }

  async materialize(fileIds) {
    const ids = validateIds(fileIds);
    const response = await this.fetch(`${this.baseUrl}/files/materialize`, {
      method: 'POST',
      headers: {
        'X-Agent-Token': this.token,
        Accept: 'application/json',
        'Content-Type': 'application/json'
      },
      body: JSON.stringify({ fileIds: ids }),
      signal: AbortSignal.timeout(300_000)
    });
    const bytes = await readBounded(response, MAX_METADATA_BYTES);
    if (!response.ok) throw new Error(`attachment_backend_http_${response.status}`);
    let records;
    try { records = JSON.parse(new TextDecoder().decode(bytes)); }
    catch { throw new Error('attachment_backend_invalid_json'); }
    if (!Array.isArray(records) || records.length !== ids.length)
      throw new Error('attachment_batch_invalid');

    const byId = new Map();
    let total = 0;
    for (const record of records) {
      validateRecord(record, this.sessionId);
      if (!ids.includes(record.id) || byId.has(record.id)) throw new Error('attachment_batch_invalid');
      total = safeAdd(total, record.size);
      if (total > this.maxTotalBytes) throw new Error('attachment_bytes_exceeded');
      byId.set(record.id, record);
    }

    const result = [];
    for (const id of ids) {
      const record = byId.get(id);
      if (!record) throw new Error('attachment_batch_invalid');
      const localPath = await this.#localPath(record);
      result.push(Object.freeze({
        id: record.id,
        name: record.name,
        mimeType: record.mimeType,
        size: record.size,
        localPath
      }));
    }
    return Object.freeze(result);
  }

  async #localPath(record) {
    if (record.storageKind === 'Pod') {
      if (record.locator !== `${record.id}/${record.name}`) throw new Error('attachment_locator_invalid');
      const existing = await this.store.head(record.id);
      if (!existing || existing.name !== record.name || existing.size !== record.size)
        throw new Error('attachment_content_unavailable');
      return existing.path;
    }

    if (record.storageKind !== 'S3' || typeof record.downloadUrl !== 'string')
      throw new Error('attachment_batch_invalid');
    let url;
    try { url = new URL(record.downloadUrl); } catch { throw new Error('attachment_download_invalid'); }
    if (!['http:', 'https:'].includes(url.protocol) || url.username || url.password)
      throw new Error('attachment_download_invalid');

    const existing = await this.store.head(record.id);
    if (existing) {
      if (existing.name !== record.name || existing.size !== record.size)
        throw new Error('attachment_content_invalid');
      return existing.path;
    }

    const response = await this.fetch(url.toString(), {
      method: 'GET', signal: AbortSignal.timeout(300_000)
    });
    if (!response.ok || !response.body) throw new Error('attachment_download_failed');
    const declared = Number(response.headers.get('content-length'));
    if (Number.isFinite(declared) && declared > record.size)
      throw new Error('attachment_bytes_exceeded');
    try {
      const stream = typeof Readable.fromWeb === 'function'
        ? Readable.fromWeb(response.body)
        : Readable.from(response.body);
      const stored = await this.store.put(record.id, record.name, stream, record.size);
      if (stored.size !== record.size) {
        await this.store.remove(record.id);
        throw new Error('attachment_content_invalid');
      }
      const ready = await this.store.head(record.id);
      if (!ready) throw new Error('attachment_content_invalid');
      return ready.path;
    } catch (error) {
      await this.store.remove(record.id).catch(() => {});
      throw error;
    }
  }
}

function validateIds(value) {
  if (!Array.isArray(value) || value.length < 1 || value.length > MAX_ATTACHMENTS)
    throw new Error('attachment_batch_invalid');
  if (value.some(id => typeof id !== 'string' || !FILE_ID.test(id)) || new Set(value).size !== value.length)
    throw new Error('attachment_batch_invalid');
  return [...value];
}

function validateRecord(record, sessionId) {
  if (!record || typeof record !== 'object' || !FILE_ID.test(record.id || '') ||
      record.sessionId !== sessionId || record.state !== 'Ready' ||
      typeof record.name !== 'string' || !record.name || record.name.length > 255 ||
      record.name.includes('/') || record.name.includes('\\') || /[\x00-\x1f]/.test(record.name) ||
      typeof record.mimeType !== 'string' || !record.mimeType ||
      !Number.isSafeInteger(record.size) || record.size < 0) {
    throw new Error('attachment_batch_invalid');
  }
}

function safeAdd(left, right) {
  const total = left + right;
  if (!Number.isSafeInteger(total)) throw new Error('attachment_bytes_exceeded');
  return total;
}

async function readBounded(response, limit) {
  if (Number(response.headers.get('content-length')) > limit)
    throw new Error('attachment_backend_response_too_large');
  if (!response.body) return new Uint8Array();
  const reader = response.body.getReader();
  const chunks = [];
  let size = 0;
  while (true) {
    const { done, value } = await reader.read();
    if (done) break;
    size += value.byteLength;
    if (size > limit) {
      await reader.cancel();
      throw new Error('attachment_backend_response_too_large');
    }
    chunks.push(value);
  }
  const result = new Uint8Array(size);
  let offset = 0;
  for (const chunk of chunks) { result.set(chunk, offset); offset += chunk.byteLength; }
  return result;
}

module.exports = { AttachmentMaterializer, validateIds };
