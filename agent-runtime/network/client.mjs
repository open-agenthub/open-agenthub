const MAX_RESPONSE_BYTES = 64 * 1024;

export class NetworkBackendClient {
  constructor(env = process.env, fetchImpl = globalThis.fetch) {
    const callbackUrl = env.AGENTHUB_CALLBACK_URL;
    const token = env.AGENTHUB_CALLBACK_TOKEN;
    if (!callbackUrl || !token) throw new Error('network_backend_not_configured');
    let parsed;
    try { parsed = new URL(callbackUrl); } catch { throw new Error('network_backend_invalid_url'); }
    if (!['http:', 'https:'].includes(parsed.protocol)) throw new Error('network_backend_invalid_url');
    this.baseUrl = `${parsed.toString().replace(/\/$/, '')}/network`;
    this.token = token;
    this.fetchImpl = fetchImpl;
  }

  requestPort(body) { return this.#request('POST', '/port-requests', body); }
  decision(id) { return this.#request('GET', `/port-requests/${encodeURIComponent(id)}`); }
  expire(id) { return this.#request('POST', `/port-requests/${encodeURIComponent(id)}/expire`); }
  listPorts() { return this.#request('GET', '/ports'); }

  async #request(method, path, body) {
    const headers = { 'X-Agent-Token': this.token, Accept: 'application/json' };
    const init = { method, headers, signal: AbortSignal.timeout(300_000) };
    if (body !== undefined) {
      headers['Content-Type'] = 'application/json';
      init.body = JSON.stringify(body);
    }
    const response = await this.fetchImpl(`${this.baseUrl}${path}`, init);
    const bytes = await readBounded(response, MAX_RESPONSE_BYTES);
    if (!response.ok) throw new Error(`network_backend_http_${response.status}`);
    if (response.status === 204 || bytes.length === 0) return null;
    try { return JSON.parse(new TextDecoder().decode(bytes)); }
    catch { throw new Error('network_backend_invalid_json'); }
  }
}

async function readBounded(response, limit) {
  if (Number(response.headers.get('content-length')) > limit)
    throw new Error('network_backend_response_too_large');
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
      throw new Error('network_backend_response_too_large');
    }
    chunks.push(value);
  }
  const result = new Uint8Array(size);
  let offset = 0;
  for (const chunk of chunks) { result.set(chunk, offset); offset += chunk.byteLength; }
  return result;
}
