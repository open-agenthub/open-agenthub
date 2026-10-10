const MAX_RESPONSE_BYTES = 64 * 1024;
// A transcript page is sized by the caller (maxChars, up to 1M characters); UTF-8 needs headroom
// above that before the guard would reject a page that was asked for.
const MAX_TRANSCRIPT_BYTES = 4 * 1024 * 1024;

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
      mode: body.mode ?? 'Interactive'
    };
    return this.#request('POST', '/api/remote/sessions', payload);
  }

  get(id) {
    return this.#request('GET', `/api/remote/sessions/${encodeURIComponent(id)}`);
  }

  /**
   * One page of a session's transcript. `offset` is the previous page's `nextOffset`, so a poller
   * transfers only what is new instead of the whole transcript on every call.
   *
   * Allowed a larger response than the other calls: a transcript page is the one reply whose size
   * the caller chooses, and the 64 KB ceiling meant for small JSON objects would reject a page the
   * caller explicitly asked for.
   */
  transcript(id, { offset, maxChars } = {}) {
    const query = new URLSearchParams();
    if (offset !== undefined) query.set('offset', String(offset));
    if (maxChars !== undefined) query.set('maxChars', String(maxChars));
    const suffix = query.size === 0 ? '' : `?${query}`;
    return this.#request(
      'GET', `/api/remote/sessions/${encodeURIComponent(id)}/transcript${suffix}`,
      undefined, MAX_TRANSCRIPT_BYTES);
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

  /** Continues a finished autonomous session interactively (docs/session-mode-conversion.md). */
  convert(id, body = {}) {
    return this.#request('POST', `/api/remote/sessions/${encodeURIComponent(id)}/convert`,
      { mode: 'interactive', ...body });
  }

  /** Owner sessions as an agent directory, optionally scoped to one project. */
  async listAgents(projectId) {
    const sessions = await this.list();
    return projectId === undefined ? sessions : sessions.filter(session => session.projectId === projectId);
  }

  /** Provider accounts, git PATs and API-key presence the token may use — never a secret. */
  credentials() {
    return this.#request('GET', '/api/remote/credentials');
  }

  /** Sends a message/task to an owned session (stored as an external message). The flags are
   * only sent when set, so a plain send looks exactly as it did before they existed. */
  sendAgentMessage(sessionId, message, { priority, interrupt } = {}) {
    const payload = { body: message };
    if (priority !== undefined) payload.priority = priority;
    if (interrupt !== undefined) payload.interrupt = interrupt;
    return this.#request('POST', `/api/remote/sessions/${encodeURIComponent(sessionId)}/messages`, payload);
  }

  // ---------------------------------------------------------------- sharing (enterprise)

  /** Direct grants and links of an owned session; never the link tokens. */
  listShares(sessionId) {
    return this.#request('GET', `${sharesPath(sessionId)}`);
  }

  shareWithUser(sessionId, recipient, role = 'Viewer') {
    return this.#request('POST', `${sharesPath(sessionId)}/users`, { recipient, role });
  }

  async unshareUser(sessionId, recipient) {
    await this.#request('DELETE', `${sharesPath(sessionId)}/users/${encodeURIComponent(recipient)}`);
    return { recipient, removed: true };
  }

  /** Mints a link; the response's `url` is the only copy of the secret. */
  createShareLink(sessionId, { role = 'Viewer', expiresAt } = {}) {
    return this.#request('POST', `${sharesPath(sessionId)}/links`, { role, expiresAt: expiresAt ?? null });
  }

  async deleteShareLink(sessionId, linkId) {
    await this.#request('DELETE', `${sharesPath(sessionId)}/links/${encodeURIComponent(linkId)}`);
    return { linkId, removed: true };
  }

  /** Sessions other owners shared with the token owner, in the sanitised shared-session shape. */
  listSharedWithMe() {
    return this.#request('GET', '/api/remote/sessions/shared');
  }

  async #request(method, path, body, maxBytes = MAX_RESPONSE_BYTES) {
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
    const bytes = await readBounded(response, maxBytes);
    if (!response.ok) {
      const error = new Error(`agenthub_http_${response.status}`);
      // The one thing read from an error body: a code the backend chose (e.g. unknown_recipient).
      // The message text is left behind on purpose — it is for people, and the tool answers with
      // a fixed vocabulary.
      const code = errorCode(bytes);
      if (code !== undefined) error.code = code;
      throw error;
    }
    if (response.status === 204 || bytes.length === 0) return null;
    try { return JSON.parse(new TextDecoder().decode(bytes)); }
    catch { throw new Error('agenthub_invalid_json'); }
  }
}

function sharesPath(sessionId) {
  return `/api/remote/sessions/${encodeURIComponent(sessionId)}/shares`;
}

function errorCode(bytes) {
  if (bytes.length === 0) return undefined;
  try {
    const parsed = JSON.parse(new TextDecoder().decode(bytes));
    return typeof parsed?.code === 'string' ? parsed.code : undefined;
  } catch {
    return undefined;
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
