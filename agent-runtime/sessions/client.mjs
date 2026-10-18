const MAX_RESPONSE_BYTES = 64 * 1024;
// Collections can exceed a single record's budget. Inbox JSON escaping can also expand
// the four-message batch beyond 64 KiB; rejecting it after delivery would hide messages.
const MAX_COLLECTION_BYTES = 4 * 1024 * 1024;

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
      mode: body.mode ?? 'Interactive',
      parentSessionId: body.parentSessionId ?? this.sessionId
    };
    return this.#request('POST', '/spawn', payload);
  }

  get(childId) {
    return this.#request('GET', `/peer/${encodeURIComponent(childId)}`);
  }

  listChildren() {
    return this.#request('GET', '/children', undefined, MAX_COLLECTION_BYTES);
  }

  async delete(childId) {
    await this.#request('DELETE', `/peer/${encodeURIComponent(childId)}`);
    return { deleted: true };
  }

  /** Only this callback identity can clear itself; there is deliberately no peer target. */
  clearContext(followUp) {
    return this.#request('POST', '/context-clear', { followUp });
  }

  /** Continues a descendant's finished autonomous run interactively (docs/session-mode-conversion.md). */
  convert(childId, body = {}) {
    return this.#request('POST', `/peer/${encodeURIComponent(childId)}/convert`,
      { mode: 'interactive', ...body });
  }

  /** Directory of this session's project fleet (slim records, includes a `self` marker). */
  listProjectAgents() {
    return this.#request('GET', '/project-agents', undefined, MAX_COLLECTION_BYTES);
  }

  /** The account this session (no id) or a descendant runs on, with its limit state and the
   * alternatives (docs/account-limits.md). */
  accountStatus(childId) {
    return this.#request('GET', childId ? `/peer/${encodeURIComponent(childId)}/account-status` : '/account-status');
  }

  /** Moves this session (no id) or a descendant to another stored account; its agent restarts with resume. */
  switchAccount(childId, credentialId) {
    return this.#request('PATCH', childId ? `/peer/${encodeURIComponent(childId)}/credential` : '/credential', { credentialId });
  }

  /** Sends a message/task to a peer agent (already resolved to a session id). The flags are
   * only sent when set, so a plain send looks exactly as it did before they existed. */
  sendAgentMessage(toSessionId, message, { priority, interrupt } = {}) {
    const payload = { to: toSessionId, body: message };
    if (priority !== undefined) payload.priority = priority;
    if (interrupt !== undefined) payload.interrupt = interrupt;
    return this.#request('POST', '/messages', payload);
  }

  /** Takes undelivered inbox messages, long-polling up to waitSeconds (0..60). */
  inbox(waitSeconds = 0) {
    const wait = Math.min(60, Math.max(0, Math.trunc(Number(waitSeconds) || 0)));
    return this.#request('GET', `/messages?wait=${wait}`, undefined, MAX_COLLECTION_BYTES);
  }

  /** The secrets this session receives, by name and delivery (docs/session-secrets.md). */
  secrets() {
    return this.#request('GET', '/secrets');
  }

  /** Asks the owner for a secret; answered at once when the session already has one of the name. */
  secretRequest(body) {
    return this.#request('POST', '/secret-requests', body);
  }

  secretRequestStatus(id) {
    return this.#request('GET', `/secret-requests/${encodeURIComponent(id)}`);
  }

  secretRequestExpire(id) {
    return this.#request('POST', `/secret-requests/${encodeURIComponent(id)}/expire`);
  }

  /** Helper containers of this session (docs/session-containers.md). */
  containerRun(body) {
    return this.#request('POST', '/containers', body);
  }

  containerList() {
    return this.#request('GET', '/containers');
  }

  containerLogs(name, { tail, previous } = {}) {
    const query = new URLSearchParams();
    if (tail !== undefined) query.set('tail', String(tail));
    if (previous) query.set('previous', 'true');
    const suffix = query.size ? `?${query}` : '';
    return this.#request('GET', `/containers/${encodeURIComponent(name)}/logs${suffix}`);
  }

  containerStop(name) {
    return this.#request('DELETE', `/containers/${encodeURIComponent(name)}`);
  }

  async #request(method, path, body, maxBytes = MAX_RESPONSE_BYTES) {
    const headers = { 'X-Agent-Token': this.token, Accept: 'application/json' };
    const init = { method, headers, signal: AbortSignal.timeout(300_000) };
    if (body !== undefined) {
      headers['Content-Type'] = 'application/json';
      init.body = JSON.stringify(body);
    }
    const response = await this.fetchImpl(`${this.baseUrl}${path}`, init);
    const bytes = await readBounded(response, maxBytes);
    if (!response.ok) {
      const error = new Error(`sessions_backend_http_${response.status}`);
      // The backend's own sentence, when it sent one; the message itself stays the stable code
      // every existing caller matches on.
      const detail = errorDetail(bytes);
      if (detail) error.detail = detail;
      throw error;
    }
    if (response.status === 204 || bytes.length === 0) return null;
    try { return JSON.parse(new TextDecoder().decode(bytes)); }
    catch { throw new Error('sessions_backend_invalid_json'); }
  }
}

function errorDetail(bytes) {
  try {
    const parsed = JSON.parse(new TextDecoder().decode(bytes));
    return typeof parsed?.error === 'string' ? parsed.error.slice(0, 1000) : undefined;
  } catch { return undefined; }
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
