/** Orchestration-safe SessionInfo fields for MCP tool responses. Never includes secrets. */
const SAFE_KEYS = [
  'id', 'title', 'description', 'owner', 'mode', 'agent', 'authMode', 'phase', 'status',
  'parentSessionId', 'projectId', 'prompt', 'systemPrompt', 'schedule', 'questionPending',
  'createdAt', 'hasMcp',
  // What session_convert changes and whether it can be called: a caller that cannot read uiMode
  // back cannot confirm it got the chat pane it asked for.
  'uiMode', 'canConvertToInteractive', 'convertedFrom',
  // The page a person opens to take the session over. An allowlist means a field the backend
  // starts returning is dropped here until it is named, so leaving this out would have made
  // session_create answer without the one thing a caller handing over a session needs.
  'url',
  // The self-deletion setting and the deadline it yields (docs/session-expiry.md).
  'autoDeleteAfterSeconds', 'autoDeleteFrom', 'expiresAt', 'lastActivityAt'
];

/**
 * Pick only orchestration-safe fields from a backend SessionInfo (or list / delete result).
 * Omits mcpConfigJson, podIp, policy, image/resources, callback tokens, browser internals.
 */
export function sanitizeSession(value) {
  if (value == null) return value;
  if (Array.isArray(value)) return value.map(sanitizeSession);
  if (typeof value !== 'object') return value;
  if (value.deleted === true && value.id === undefined) return { deleted: true };

  const out = {};
  for (const key of SAFE_KEYS) {
    if (value[key] !== undefined) out[key] = value[key];
  }
  if (typeof value.timedOut === 'boolean') out.timedOut = value.timedOut;

  const repos = pickRepos(value);
  if (repos) out.repos = repos;

  return out;
}

function pickRepos(info) {
  if (Array.isArray(info.repos)) {
    const repos = [];
    for (const repo of info.repos) {
      if (!repo || typeof repo !== 'object' || typeof repo.url !== 'string') continue;
      const entry = { url: repo.url };
      if (typeof repo.branch === 'string') entry.branch = repo.branch;
      // The provider id is the one thing that says *how* a repo is authenticated. Dropping it
      // meant a caller could send it but never read it back, so it could not confirm the session
      // was created the way it asked. It is an id, not a secret — the token stays server-side.
      if (typeof repo.providerId === 'string') entry.providerId = repo.providerId;
      repos.push(entry);
    }
    return repos;
  }
  if (typeof info.repoUrl === 'string' && info.repoUrl.length > 0) {
    return [{ url: info.repoUrl }];
  }
  return undefined;
}
