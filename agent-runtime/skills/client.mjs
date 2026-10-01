// The hub side of the skills proxy: one JSON-RPC forward and one archive download.
//
// The proxy speaks the same protocol the hub's skill-library MCP endpoint already speaks,
// so a message it has no business touching is passed on unchanged — a tool added to the
// hub later works through the proxy without the proxy knowing about it.

const MAX_RESPONSE_BYTES = 4_000_000;

export class SkillsBackendClient {
  constructor(env = process.env, fetchImpl = globalThis.fetch) {
    const callbackUrl = env.AGENTHUB_CALLBACK_URL;
    const token = env.AGENTHUB_CALLBACK_TOKEN;
    if (!callbackUrl || !token) throw new Error('skills_backend_not_configured');
    let parsed;
    try { parsed = new URL(callbackUrl); } catch { throw new Error('skills_backend_invalid_url'); }
    if (!['http:', 'https:'].includes(parsed.protocol)) throw new Error('skills_backend_invalid_url');
    this.baseUrl = parsed.toString().replace(/\/$/, '');
    this.token = token;
    this.fetchImpl = fetchImpl;
  }

  /** Forwards one JSON-RPC message and returns the hub's reply (null for a notification). */
  async rpc(message) {
    const response = await this.fetchImpl(`${this.baseUrl}/mcp`, {
      method: 'POST',
      headers: {
        'X-Agent-Token': this.token,
        'Content-Type': 'application/json',
        Accept: 'application/json'
      },
      body: JSON.stringify(message),
      signal: AbortSignal.timeout(120_000)
    });
    if (response.status === 202) return null;
    const bytes = await readBounded(response, MAX_RESPONSE_BYTES);
    if (!response.ok) throw new Error(`skills_backend_http_${response.status}`);
    if (bytes.length === 0) return null;
    try { return JSON.parse(new TextDecoder().decode(bytes)); }
    catch { throw new Error('skills_backend_invalid_json'); }
  }

  /**
   * Downloads a skill as a tar.gz (SKILL.md plus every extra file).
   *
   * A plain hub route authenticated with the session token, not a presigned storage url:
   * the signature on those expires while the agent is still working, which is the failure
   * the session-files route documents. It also means the download works on an instance
   * with no object storage at all, where the content lives in the database.
   */
  async bundle(nameOrId, version) {
    const query = version ? `?version=${encodeURIComponent(version)}` : '';
    const url = `${this.baseUrl}/skills/${encodeURIComponent(nameOrId)}/files.tar.gz${query}`;
    const response = await this.fetchImpl(url, {
      method: 'GET',
      headers: { 'X-Agent-Token': this.token },
      signal: AbortSignal.timeout(300_000)
    });
    if (response.status === 404) throw new Error('skills_skill_not_found');
    if (!response.ok) throw new Error(`skills_backend_http_${response.status}`);
    return Buffer.from(await readBounded(response, MAX_RESPONSE_BYTES));
  }
}

async function readBounded(response, limit) {
  if (Number(response.headers.get('content-length')) > limit)
    throw new Error('skills_backend_response_too_large');
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
      throw new Error('skills_backend_response_too_large');
    }
    chunks.push(value);
  }
  const result = new Uint8Array(size);
  let offset = 0;
  for (const chunk of chunks) { result.set(chunk, offset); offset += chunk.byteLength; }
  return result;
}
