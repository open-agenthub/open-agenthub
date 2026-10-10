/** Orchestration-safe SessionInfo fields for MCP tool responses. Never includes secrets. */
const SAFE_KEYS = [
  'id', 'title', 'description', 'owner', 'mode', 'agent', 'authMode', 'phase', 'status',
  'parentSessionId', 'projectId', 'prompt', 'schedule', 'questionPending',
  'createdAt', 'hasMcp', 'autoApprove'
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
      // Not a secret, and the only way a caller sees whether a repository is authenticated.
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
