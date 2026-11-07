// Account-aware model lists for the model picker (#461, docs/design/model-discovery.md). The hub
// answers { freshness, fetchedAt, catalog, selected, selectedListed }; everything here only
// turns that into what the picker says. Choosing stays free text: a list is never required.

// OpenClaw picks its model from the provider key at start; nothing would pass a choice on.
export function supportsModelChoice(agent) {
  return ['Claude', 'Codex', 'Cursor', 'OpenCode'].includes(agent)
}

/** The query naming one account's list, or null while the choice is incomplete. */
export function catalogQuery({ agent, authMode, credentialId, modelEndpointUrl }) {
  if (!supportsModelChoice(agent) || !authMode) return null
  const query = { agent, authMode }
  if (authMode === 'CustomEndpoint') {
    const url = String(modelEndpointUrl || '').trim()
    if (!/^https?:\/\/\S+$/.test(url)) return null
    query.endpointUrl = url
  } else if (credentialId) {
    query.credentialId = credentialId
  }
  return query
}

function age(fetchedAt, now) {
  const minutes = Math.max(0, Math.round((now - Date.parse(fetchedAt)) / 60000))
  if (minutes < 2) return 'just now'
  if (minutes < 90) return `${minutes} min ago`
  const hours = Math.round(minutes / 60)
  if (hours < 36) return `${hours} h ago`
  return `${Math.round(hours / 24)} days ago`
}

/**
 * One line saying where the list comes from and how far to trust it. `tone` is ok, warn or
 * muted; the picker colours the line with it.
 */
export function catalogStatus(view, now = Date.now()) {
  if (!view || view.freshness === 'unknown' || !view.catalog) {
    return { tone: 'muted', text: 'No list for this account yet. It is read the first time a session runs on it; type a model id or leave it empty for the default.' }
  }
  const { catalog } = view
  const when = view.fetchedAt ? age(view.fetchedAt, now) : ''
  const stale = view.freshness === 'stale' ? ` — may be out of date (${when})` : ` (${when})`
  if (catalog.state === 'error') {
    const reason = catalog.error?.message || 'the probe failed'
    return { tone: 'warn', text: `Could not read this account's models: ${reason}${stale}. Type a model id instead.` }
  }
  if (catalog.state === 'unsupported') return { tone: 'muted', text: 'This agent reports no model list. Type a model id.' }
  if (catalog.state === 'fallback') {
    return { tone: 'warn', text: `The account's list was refused; these are the CLI's built-in models, which this account may not be able to use${stale}.` }
  }
  const from = catalog.source === 'endpoint' ? 'Models the endpoint lists' : catalog.verified ? "This account's models" : "The CLI's models, not checked against this account"
  return { tone: view.freshness === 'stale' ? 'warn' : 'ok', text: `${from}${stale}.` }
}

/** The models to offer, default first, as the catalog listed them otherwise. */
export function modelOptions(view) {
  const models = view?.catalog?.state === 'error' ? [] : (view?.catalog?.models || [])
  return [...models].sort((a, b) => Number(Boolean(b.isDefault)) - Number(Boolean(a.isDefault)))
}

function tokens(count) {
  if (count >= 1_000_000) return `${Math.round(count / 100_000) / 10}M`
  if (count >= 1000) return `${Math.round(count / 1000)}k`
  return String(count)
}

/** The controls a model offers, in a few words, or '' when the CLI reported none. */
export function capabilityText(model) {
  const caps = model?.capabilities || {}
  const parts = []
  if (caps.reasoningEfforts?.length) parts.push(`effort ${caps.reasoningEfforts.join('/')}`)
  if (caps.contextWindow) parts.push(`${tokens(caps.contextWindow)} context`)
  if (caps.imageInput === true) parts.push('images')
  if (caps.fastMode === true) parts.push('fast mode')
  if (caps.toolCall === false) parts.push('no tools')
  return parts.join(' · ')
}

/** Shown under the field when the typed or stored id is not in a list that loaded. */
export function unlistedNote(view, modelName) {
  const id = String(modelName || '').trim()
  if (!id) return ''
  const models = modelOptions(view)
  if (!models.length || models.some(model => model.id === id || model.resolvedId === id)) return ''
  return `“${id}” is not in this account's list. It is kept as typed; the session fails at its first turn if the account cannot use it.`
}
