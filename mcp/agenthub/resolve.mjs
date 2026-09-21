/**
 * Resolves an agent reference — a session id or a unique agent title — against the
 * project directory. Throws with a stable `code` and a `candidates` list on failure:
 *   - agent_not_found       (candidates = the whole directory)
 *   - agent_title_ambiguous (candidates = the sessions sharing the title)
 */
export function resolveAgentTarget(agents, to) {
  const list = (Array.isArray(agents) ? agents : []).filter(a => a && typeof a.id === 'string');
  const ref = String(to ?? '').trim();

  const byId = list.find(agent => agent.id === ref);
  if (byId) return byId.id;

  const needle = ref.toLowerCase();
  const matches = list.filter(agent => String(agent.title ?? '').trim().toLowerCase() === needle);
  if (matches.length === 1) return matches[0].id;

  const code = matches.length === 0 ? 'agent_not_found' : 'agent_title_ambiguous';
  const error = new Error(code);
  error.code = code;
  error.candidates = (matches.length === 0 ? list : matches)
    .map(agent => ({ id: agent.id, title: agent.title }));
  throw error;
}
