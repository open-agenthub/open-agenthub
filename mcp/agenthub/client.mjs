const MAX_RESPONSE_BYTES = 64 * 1024;

export class AgentHubClient {
  constructor(env = process.env, fetchImpl = globalThis.fetch) {
    const baseUrl = env.AGENTHUB_URL;
    const token = env.AGENTHUB_TOKEN;
    if (!baseUrl || !token) throw new Error('agenthub_not_configured');
    let parsed;
    try { parsed = new URL(baseUrl); } catch { throw new Error('agenthub_invalid_url'); }
    if (!['http:', 'https:'].includes(parsed.protocol)) throw new Error('agenthub_invalid_url');
    this.baseUrl = parsed.toString().replace(/\/$/, '');
    this.token = token;
    this.fetchImpl = fetchImpl;
  }

  create(body = {}) {
    const payload = {
      ...body,
      mode: body.mode ?? 'Autonomous'
    };
    return this.#request('POST', '/api/remote/sessions', payload);
  }

  get(id) {
    return this.#request('GET', `/api/remote/sessions/${encodeURIComponent(id)}`);
  }

  async list(filters = {}) {
    const sessions = await this.#request('GET', '/api/remote/sessions');
    const list = Array.isArray(sessions) ? sessions : [];
    return list.filter(session => {
      if (filters.parentSessionId !== undefined && session.parentSessionId !== filters.parentSessionId) {
        return false;
      }
      if (filters.phase !== undefined && session.phase !== filters.phase) {
        return false;
      }
      return true;
    });
  }

  async delete(id) {
    await this.#request('DELETE', `/api/remote/sessions/${encodeURIComponent(id)}`);
    return { deleted: true };
  }

  async #request(method, path, body) {
    const headers = {
      Authorization: `Bearer ${this.token}`,
      Accept: 'application/json'
    };
    const init = { method, headers, signal: AbortSignal.timeout(300_000) };
    if (body !== undefined) {
      headers['Content-Type'] = 'application/json';
      init.body = JSON.stringify(body);
    }
    const response = await this.fetchImpl(`${this.baseUrl}${path}`, init);
    const bytes = await readBounded(response, MAX_RESPONSE_BYTES);
    if (!response.ok) throw new Error(`agenthub_http_${response.status}`);
    if (response.status === 204 || bytes.length === 0) return null;
    try { return JSON.parse(new TextDecoder().decode(bytes)); }
    catch { throw new Error('agenthub_invalid_json'); }
  }
}

async function readBounded(response, limit) {
  if (Number(response.headers.get('content-length')) > limit)
    throw new Error('agenthub_response_too_large');
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
      throw new Error('agenthub_response_too_large');
    }
    chunks.push(value);
  }
  const result = new Uint8Array(size);
  let offset = 0;
  for (const chunk of chunks) { result.set(chunk, offset); offset += chunk.byteLength; }
  return result;
}
