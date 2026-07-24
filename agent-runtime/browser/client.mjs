const MAX_RESPONSE_BYTES = 64 * 1024;

export class BrowserBackendClient {
  constructor(env = process.env, fetchImpl = globalThis.fetch) {
    const callbackUrl = env.AGENTHUB_CALLBACK_URL;
    const token = env.AGENTHUB_CALLBACK_TOKEN;
    if (!callbackUrl || !token) throw new Error('browser_backend_not_configured');
    let parsed;
    try { parsed = new URL(callbackUrl); } catch { throw new Error('browser_backend_invalid_url'); }
    if (!['http:', 'https:'].includes(parsed.protocol)) throw new Error('browser_backend_invalid_url');
    this.url = `${parsed.toString().replace(/\/$/, '')}/browser`;
    this.token = token;
    this.fetchImpl = fetchImpl;
  }

  start() { return this.#request('POST'); }
  status() { return this.#request('GET'); }
  async stop() { await this.#request('DELETE'); return { phase: 'Stopped' }; }

  async #request(method) {
    const response = await this.fetchImpl(this.url, {
      method,
      headers: { 'X-Agent-Token': this.token, Accept: 'application/json' },
      signal: AbortSignal.timeout(95_000)
    });
    const bytes = await readBounded(response, MAX_RESPONSE_BYTES);
    if (!response.ok) throw new Error(`browser_backend_http_${response.status}`);
    if (response.status === 204 || bytes.length === 0) return null;
    try { return JSON.parse(new TextDecoder().decode(bytes)); }
    catch { throw new Error('browser_backend_invalid_json'); }
  }
}

async function readBounded(response, limit) {
  if (Number(response.headers.get('content-length')) > limit)
    throw new Error('browser_backend_response_too_large');
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
      throw new Error('browser_backend_response_too_large');
    }
    chunks.push(value);
  }
  const result = new Uint8Array(size);
  let offset = 0;
  for (const chunk of chunks) { result.set(chunk, offset); offset += chunk.byteLength; }
  return result;
}