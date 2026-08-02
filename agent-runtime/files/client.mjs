const MAX_RESPONSE_BYTES = 1_000_000;

export class FilesBackendClient {
  constructor(env = process.env, fetchImpl = globalThis.fetch) {
    const callbackUrl = env.AGENTHUB_CALLBACK_URL;
    const token = env.AGENTHUB_CALLBACK_TOKEN;
    if (!callbackUrl || !token) throw new Error('files_backend_not_configured');
    let parsed;
    try { parsed = new URL(callbackUrl); } catch { throw new Error('files_backend_invalid_url'); }
    if (!['http:', 'https:'].includes(parsed.protocol)) throw new Error('files_backend_invalid_url');
    this.baseUrl = parsed.toString().replace(/\/$/, '');
    this.origin = parsed.origin;
    this.token = token;
    this.fetchImpl = fetchImpl;
  }

  capabilities() { return this.#json('GET', '/files/capabilities'); }
  list() { return this.#json('GET', '/files'); }
  materialize(fileIds) { return this.#json('POST', '/files/materialize', { fileIds }); }
  reserve(body) { return this.#json('POST', '/files/reserve', { ...body, source: 'agent' }); }
  complete(fileId) { return this.#json('POST', `/files/${encodeURIComponent(fileId)}/complete`); }
  present(fileId) { return this.#json('PUT', '/files/presentation', { fileId }); }
  dismiss() { return this.#json('PUT', '/files/presentation', { fileId: null }); }

  async upload(descriptor, body, mimeType) {
    if (!descriptor || !['proxy', 'presigned'].includes(descriptor.kind) || !descriptor.url)
      throw new Error('files_upload_descriptor_invalid');
    const url = new URL(descriptor.url, `${this.origin}/`).toString();
    const headers = { ...(descriptor.headers ?? {}) };
    if (!Object.keys(headers).some(key => key.toLowerCase() === 'content-type'))
      headers['Content-Type'] = mimeType;
    if (descriptor.kind === 'proxy') headers['X-Agent-Token'] = this.token;
    const init = { method: 'PUT', headers, body, signal: AbortSignal.timeout(300_000) };
    if (body && typeof body.pipe === 'function') init.duplex = 'half';
    const response = await this.fetchImpl(url, init);
    if (!response.ok) throw new Error(`files_backend_http_${response.status}`);
  }

  async #json(method, path, body) {
    const headers = { 'X-Agent-Token': this.token, Accept: 'application/json' };
    const init = { method, headers, signal: AbortSignal.timeout(300_000) };
    if (body !== undefined) {
      headers['Content-Type'] = 'application/json';
      init.body = JSON.stringify(body);
    }
    const response = await this.fetchImpl(`${this.baseUrl}${path}`, init);
    const bytes = await readBounded(response, MAX_RESPONSE_BYTES);
    if (!response.ok) throw new Error(`files_backend_http_${response.status}`);
    if (response.status === 204 || bytes.length === 0) return null;
    try { return JSON.parse(new TextDecoder().decode(bytes)); }
    catch { throw new Error('files_backend_invalid_json'); }
  }
}

async function readBounded(response, limit) {
  if (Number(response.headers.get('content-length')) > limit)
    throw new Error('files_backend_response_too_large');
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
      throw new Error('files_backend_response_too_large');
    }
    chunks.push(value);
  }
  const result = new Uint8Array(size);
  let offset = 0;
  for (const chunk of chunks) { result.set(chunk, offset); offset += chunk.byteLength; }
  return result;
}
