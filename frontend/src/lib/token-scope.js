// A personal API token's credential restriction (docs/credential-scopes.md), as the settings
// page edits it. The wire form is an allow list — { providerAccounts: { Claude: [ids|'*'] },
// gitPats: [ids|'*'], apiKeys: bool } or null for "anything" — and the draft the checkboxes
// bind to is the same shape with every list spelled out.

export const AGENTS = ['Claude', 'Codex', 'Cursor', 'OpenClaw']

/** The accounts of one agent out of the `{ Claude: [...] }` listing. */
function listFor(accounts, agent) {
  const list = accounts && typeof accounts === 'object' ? accounts[agent] : null
  return Array.isArray(list) ? list : []
}

/** An empty draft: nothing ticked, so a token restricted "to nothing" cannot start a session. */
export function emptyScopeDraft() {
  return { providerAccounts: Object.fromEntries(AGENTS.map(agent => [agent, []])), gitPats: [], apiKeys: false }
}

/** The draft for an existing restriction; '*' ticks every stored entry at the time of editing. */
export function draftFromScope(scope, accounts, pats) {
  const draft = emptyScopeDraft()
  if (!scope) return draft
  for (const agent of AGENTS) {
    const entry = Object.entries(scope.providerAccounts || {})
      .find(([name]) => name.toLowerCase() === agent.toLowerCase())
    const ids = entry ? entry[1] : []
    draft.providerAccounts[agent] = ids.includes('*')
      ? listFor(accounts, agent).map(account => account.id)
      : listFor(accounts, agent).filter(account => ids.includes(account.id)).map(account => account.id)
  }
  const patIds = Array.isArray(scope.gitPats) ? scope.gitPats : []
  draft.gitPats = patIds.includes('*')
    ? (pats || []).map(pat => pat.id)
    : (pats || []).filter(pat => patIds.includes(pat.id)).map(pat => pat.id)
  draft.apiKeys = scope.apiKeys === true
  return draft
}

/** The wire form of a draft: only agents with a ticked account are named, so an unticked agent is not allowed. */
export function scopeFromDraft(draft) {
  const providerAccounts = {}
  for (const agent of AGENTS) {
    const ids = draft?.providerAccounts?.[agent] || []
    if (ids.length) providerAccounts[agent] = [...ids]
  }
  return {
    providerAccounts,
    gitPats: [...(draft?.gitPats || [])],
    apiKeys: !!draft?.apiKeys
  }
}

/** True when the draft allows something; a restriction that allows nothing is a mistake worth flagging. */
export function draftAllowsAnything(draft) {
  return AGENTS.some(agent => (draft?.providerAccounts?.[agent] || []).length > 0)
    || (draft?.gitPats || []).length > 0 || !!draft?.apiKeys
}

/**
 * The short form shown in the token list, e.g. "Claude: Work · git: 2 of 3 · API keys", or
 * "unrestricted". Labels come from the current listing; an id whose account is gone is counted
 * but not named.
 */
export function scopeSummary(scope, accounts, pats) {
  if (!scope) return 'unrestricted'
  const parts = []
  for (const [name, ids] of Object.entries(scope.providerAccounts || {})) {
    if (!Array.isArray(ids) || !ids.length) continue
    const agent = AGENTS.find(a => a.toLowerCase() === name.toLowerCase()) || name
    if (ids.includes('*')) { parts.push(`${agent}: any`); continue }
    const labels = ids.map(id => listFor(accounts, agent).find(account => account.id === id)?.label || id)
    parts.push(`${agent}: ${labels.join(', ')}`)
  }
  const patIds = Array.isArray(scope.gitPats) ? scope.gitPats : []
  if (patIds.includes('*')) parts.push('git: any')
  else if (patIds.length) parts.push(`git: ${patIds.length} of ${(pats || []).length}`)
  if (scope.apiKeys === true) parts.push('API keys')
  return parts.length ? parts.join(' · ') : 'nothing allowed'
}
