const MAX_RESPONSE_BYTES = 64 * 1024;

export class SessionsBackendClient {
  constructor(env = process.env, fetchImpl = globalThis.fetch) {
    const callbackUrl = env.AGENTHUB_CALLBACK_URL;
    const token = env.AGENTHUB_CALLBACK_TOKEN;
    const sessionId = env.AGENTHUB_SESSION_ID;
    if (!callbackUrl || !token || !sessionId) throw new Error('sessions_backend_not_configured');
    let parsed;
    try { parsed = new URL(callbackUrl); } catch { throw new Error('sessions_backend_invalid_url'); }
    if (!['http:', 'https:'].includes(parsed.protocol)) throw new Error('sessions_backend_invalid_url');
    this.baseUrl = parsed.toString().replace(/\/$/, '');
    this.token = token;
    this.sessionId = sessionId;
    this.fetchImpl = fetchImpl;
  }

  create(body = {}) {
    const payload = {
      ...body,
      mode: body.mode ?? 'Autonomous',
      parentSessionId: body.parentSessionId ?? this.sessionId
    };
    return this.#request('POST', '/spawn', payload);
  }

  get(childId) {
    return this.#request('GET', `/peer/${encodeURIComponent(childId)}`);
  }

  listChildren() {
    return this.#request('GET', '/children');
  }

  async delete(childId) {
    await this.#request('DELETE', `/peer/${encodeURIComponent(childId)}`);
    return { deleted: true };
  }

  /** Directory of this session's project fleet (slim records, includes a `self` marker). */
  listProjectAgents() {
    return this.#request('GET', '/project-agents');
  }

  /** Sends a message/task to a peer agent (already resolved to a session id). */
  sendAgentMessage(toSessionId, message) {
    return this.#request('POST', '/messages', { to: toSessionId, body: message });
  }

  /** Takes undelivered inbox messages, long-polling up to waitSeconds (0..60). */
  inbox(waitSeconds = 0) {
    const wait = Math.min(60, Math.max(0, Math.trunc(Number(waitSeconds) || 0)));
    return this.#request('GET', `/messages?wait=${wait}`);
  }

  async #request(method, path, body) {
    const headers = { 'X-Agent-Token': this.token, Accept: 'application/json' };
    const init = { method, headers, signal: AbortSignal.timeout(300_000) };
    if (body !== undefined) {
      headers['Content-Type'] = 'application/json';
      init.body = JSON.stringify(body);
    }
    const response = await this.fetchImpl(`${this.baseUrl}${path}`, init);
    const bytes = await readBounded(response, MAX_RESPONSE_BYTES);
    if (!response.ok) throw new Error(`sessions_backend_http_${response.status}`);
    if (response.status === 204 || bytes.length === 0) return null;
    try { return JSON.parse(new TextDecoder().decode(bytes)); }
    catch { throw new Error('sessions_backend_invalid_json'); }
  }
}

async function readBounded(response, limit) {
  if (Number(response.headers.get('content-length')) > limit)
    throw new Error('sessions_backend_response_too_large');
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
      throw new Error('sessions_backend_response_too_large');
    }
    chunks.push(value);
  }
  const result = new Uint8Array(size);
  let offset = 0;
  for (const chunk of chunks) { result.set(chunk, offset); offset += chunk.byteLength; }
  return result;
}
