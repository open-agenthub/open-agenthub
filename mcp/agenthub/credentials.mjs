/**
 * Reads an id list that arrives as text. The MCP tools take `gitPatIds` as a comma-separated
 * string rather than a declared array for the same reason the remote server takes its flags as
 * strings: a client caches the tool schema when it connects, and a parameter added later arrives
 * as a string from every already-connected client.
 *
 * Empty or "*" means "not specified" — the backend then uses every stored token, as before —
 * and "none" is the one way to say "no token at all", since an empty string cannot carry it.
 */
export function parseIdList(value) {
  if (value === undefined || value === null) return undefined;
  const text = String(value).trim();
  if (text === '' || text === '*') return undefined;
  if (text.toLowerCase() === 'none') return [];
  return [...new Set(text.split(',').map(item => item.trim()).filter(Boolean))];
}

/** The create body with the text-form credential parameters turned into what the REST API takes. */
export function withCredentialSelection(body) {
  const { credentialId, gitPatIds, ...rest } = body ?? {};
  const out = { ...rest };
  if (typeof credentialId === 'string' && credentialId.trim()) out.credentialId = credentialId.trim();
  const ids = parseIdList(gitPatIds);
  if (ids !== undefined) out.gitPatIds = ids;
  return out;
}
