export const agentOptions = [
  { value: 'Claude', label: 'Claude', hint: 'Anthropic agent runtime' },
  { value: 'Codex', label: 'Codex', hint: 'OpenAI agent runtime' },
  { value: 'Cursor', label: 'Cursor', hint: 'Cursor agent runtime' },
  { value: 'OpenClaw', label: 'OpenClaw', hint: 'OpenClaw agent runtime' },
  { value: 'OpenCode', label: 'OpenCode', hint: 'OpenCode agent runtime (OpenCode Go)' }
]

/** Filter the agent catalog by an allowlist. Empty/missing = unrestricted. */
export function filterAgentOptions(allowed, { include } = {}) {
  const list = Array.isArray(allowed) ? allowed : []
  const filtered = !list.length
    ? agentOptions.map(option => ({ ...option }))
    : agentOptions.filter(option => list.includes(option.value))
  if (include && !filtered.some(option => option.value === include)) {
    const extra = agentOptions.find(option => option.value === include)
    if (extra) filtered.push({ ...extra })
  }
  return filtered
}

export const openClawApiKeySourceOptions = [
  { value: 'Anthropic', label: 'Anthropic', hint: 'Use stored Anthropic API key' },
  { value: 'OpenAI', label: 'OpenAI', hint: 'Use stored OpenAI API key' },
  { value: 'Cursor', label: 'Cursor', hint: 'Use stored Cursor API key' }
]

const PUBLIC_AUTH_OPTIONS = [
  { value: 'Subscription', label: 'Subscription', hint: 'Use your provider plan login' },
  { value: 'ApiKey', label: 'API key', hint: 'Use provider API billing' }
]

// A self-hosted model server instead of a provider (docs/custom-model-endpoints.md). Only the
// agents the backend accepts it for; each needs a different wire protocol, which the hint names
// because "OpenAI-compatible" alone is not enough for two of them.
const CUSTOM_ENDPOINT_OPTION = { value: 'CustomEndpoint', label: 'Own endpoint', hint: 'Use a self-hosted model server' }
const CUSTOM_ENDPOINT_PROTOCOLS = {
  Claude: 'Claude Code speaks the Anthropic Messages API: the server must offer /v1/messages. An OpenAI-only server will not work.',
  Codex: 'Codex speaks only the OpenAI Responses API: the server must offer /v1/responses. /v1/chat/completions alone will not work.',
  OpenCode: 'OpenCode speaks OpenAI Chat Completions: the server must offer /v1/chat/completions.'
}

export function supportsCustomEndpoint(agent) {
  return Object.hasOwn(CUSTOM_ENDPOINT_PROTOCOLS, agent)
}

export function customEndpointProtocol(agent) {
  return CUSTOM_ENDPOINT_PROTOCOLS[agent] || ''
}

export function needsOpenClawApiKeySource(agent, authMode) {
  return agent === 'OpenClaw' && authMode === 'ApiKey'
}

export function authOptions(agent, legacyMode) {
  const options = PUBLIC_AUTH_OPTIONS.map(option => ({ ...option }))
  if (supportsCustomEndpoint(agent)) options.push({ ...CUSTOM_ENDPOINT_OPTION })
  return agent === 'Claude' && legacyMode === 'Auto'
    ? [{ value: 'Auto', label: 'Auto (legacy)', hint: 'Existing migrated selection' }, ...options]
    : options
}

export function defaultPolicy(agent) {
  if (agent === 'Codex') {
    return {
      allowedTools: ['Read', 'Edit'],
      allowedMcpTools: [],
      allowedCommands: ['git status', 'npm test', 'dotnet test']
    }
  }
  if (agent === 'OpenCode') {
    // OpenCode's tools arrive under the hub's names (bash → Bash, edit/apply_patch → Edit, …),
    // and shell commands are matched by prefix like Codex's, so the same shape applies.
    return {
      allowedTools: ['Read', 'Edit', 'Glob', 'Grep'],
      allowedMcpTools: [],
      allowedCommands: ['git status', 'npm test']
    }
  }
  if (agent === 'Cursor') {
    return {
      allowedTools: ['Shell(git status)', 'Read(**)', 'Write(**)'],
      allowedMcpTools: [],
      allowedCommands: []
    }
  }
  return {
    allowedTools: ['Edit', 'Bash(git*)', 'Read'],
    allowedMcpTools: [],
    allowedCommands: []
  }
}

function populatedPolicy(source, agent) {
  if (source.policy && typeof source.policy === 'object') {
    return {
      allowedTools: source.policy.allowedTools || [],
      allowedMcpTools: source.policy.allowedMcpTools || [],
      allowedCommands: source.policy.allowedCommands || []
    }
  }
  if (Array.isArray(source.allowedTools) && source.allowedTools.length) {
    return { allowedTools: source.allowedTools, allowedMcpTools: [], allowedCommands: [] }
  }
  return defaultPolicy(agent)
}

export function defaultAgentForm(source = {}) {
  const agent = source.agent || 'Claude'
  const policy = populatedPolicy(source, agent)
  return {
    agent,
    authMode: source.authMode || 'Subscription',
    openClawApiKeySource: source.openClawApiKeySource || 'Anthropic',
    modelEndpointUrl: source.modelEndpointUrl || '',
    modelName: source.modelName || '',
    allowedToolsRaw: policy.allowedTools.join('\n'),
    allowedMcpToolsRaw: policy.allowedMcpTools.join('\n'),
    allowedCommandsRaw: policy.allowedCommands.join('\n')
  }
}

export function agentPayload(form) {
  const payload = {
    agent: form.agent,
    authMode: form.authMode
  }
  if (needsOpenClawApiKeySource(form.agent, form.authMode)) {
    payload.openClawApiKeySource = form.openClawApiKeySource || 'Anthropic'
  }
  // Only with the mode: the backend refuses endpoint fields on any other one.
  if (form.authMode === 'CustomEndpoint') {
    payload.modelEndpointUrl = String(form.modelEndpointUrl || '').trim()
    payload.modelName = String(form.modelName || '').trim()
  }
  return payload
}

function lines(value) {
  return [...new Set(String(value || '').split(/\r?\n/).map(item => item.trim()).filter(Boolean))]
}

/** Parse form policy fields without agent-specific submit transforms (e.g. Cursor force-empty commands). */
export function policyFromForm(form) {
  return {
    allowedTools: lines(form.allowedToolsRaw),
    allowedMcpTools: lines(form.allowedMcpToolsRaw),
    allowedCommands: lines(form.allowedCommandsRaw)
  }
}

export function policyPayload(form) {
  const policy = policyFromForm(form)
  if (form.agent === 'Cursor') policy.allowedCommands = []
  return policy
}

export function toolsPlaceholder(agent) {
  if (agent === 'Cursor') return 'Shell(git status)\nRead(**)\nWrite(**)'
  if (agent === 'Codex') return 'Read\nEdit'
  if (agent === 'OpenCode') return 'Read\nEdit\nGlob\nGrep'
  return 'Read\nEdit\nBash(git*)'
}

export function commandsPlaceholder(agent) {
  return agent === 'Codex' || agent === 'OpenCode' ? 'git status\nnpm test\ndotnet test' : 'git status\nnpm test'
}

/** The accounts stored for one agent out of the `{ Claude: [...], ... }` listing. */
export function accountsFor(accounts, agent) {
  const list = accounts && typeof accounts === 'object' ? accounts[agent] : null
  return Array.isArray(list) ? list : []
}

/** The account a session without an explicit choice runs on: the default, else the first. */
export function defaultAccountId(list) {
  const accounts = Array.isArray(list) ? list : []
  return (accounts.find(account => account?.isDefault) || accounts[0])?.id || ''
}

/** "Work — me@example.com · Example Org": the label plus whatever identity the login carried. */
export function accountOptionLabel(account) {
  if (!account) return ''
  const identity = [account.email, account.organization].filter(Boolean).join(' · ')
  return identity ? `${account.label} — ${identity}` : account.label
}

/** Whether a listing entry is at its usage limit right now (docs/account-limits.md). */
export function isAccountExhausted(account) {
  return !!account?.isExhausted
}

/** "at limit · resets 14:00": what the badges and dropdowns say about an exhausted account. */
export function accountLimitLabel(account) {
  if (!isAccountExhausted(account)) return ''
  const until = account.exhaustedUntil ? new Date(account.exhaustedUntil) : null
  if (!until || Number.isNaN(until.getTime())) return 'at limit'
  const time = until.toLocaleTimeString([], { hour: '2-digit', minute: '2-digit' })
  return `at limit · resets ${time}`
}

/** The accounts of a list a session could be moved to: not the current one, not exhausted. */
export function availableAlternatives(list, currentId) {
  return (Array.isArray(list) ? list : []).filter(account => account?.id !== currentId && !isAccountExhausted(account))
}

/** The stored git PATs ({id, kind, host, label, isDefault, …}) out of the credential status; never a token. */
export function gitPatOptions(status) {
  return Array.isArray(status?.gitPats) ? status.gitPats : []
}

/**
 * The tokens grouped by host, in stored order: [{host, kind, tokens}]. A session uses at most one
 * per host (git's store answers with the first line for a host), so the picker offers one choice
 * per group rather than a checkbox per token.
 */
export function gitPatHosts(options) {
  const hosts = new Map()
  for (const pat of options) {
    if (!hosts.has(pat.host)) hosts.set(pat.host, { host: pat.host, kind: pat.kind, tokens: [] })
    hosts.get(pat.host).tokens.push(pat)
  }
  return [...hosts.values()]
}

/** Each host's default token: the one marked, or the first for a backend that marks none. */
export function gitPatDefaults(options) {
  return gitPatHosts(options).map(group => (group.tokens.find(pat => pat.isDefault) || group.tokens[0]).id)
}

/** How a token is named in a picker: its label, else its host. */
export function gitPatName(pat) {
  return pat?.label || pat?.host || ''
}

/**
 * The tokens a session actually gets from its stored selection: null is every host's default, a
 * list keeps what still exists, one per host.
 */
export function gitPatSelectionFor(current, options) {
  if (!Array.isArray(current)) return gitPatDefaults(options)
  const seen = new Set()
  return options.filter(pat => current.includes(pat.id) && !seen.has(pat.host) && seen.add(pat.host)).map(pat => pat.id)
}

function defaultsChosen(selected, options) {
  const defaults = gitPatDefaults(options)
  return options.length > 0 && defaults.length === selected.length && defaults.every(id => selected.includes(id))
}

/**
 * What a create sends: null when every host has its default chosen — "the defaults, including
 * hosts added later", which is what a session got before a choice existed — else the chosen ids.
 */
export function gitPatIdsPayload(selected, options) {
  if (!options.length || defaultsChosen(selected, options)) return null
  return options.map(pat => pat.id).filter(id => selected.includes(id))
}

/**
 * What an edit or duplicate sends: nothing when the selection matches the session's, `['*']`
 * when a restricted session goes back to the defaults (null would mean "unchanged" there), else
 * the chosen ids.
 */
export function gitPatIdsChange(selected, options, current) {
  if (!options.length) return {}
  const defaults = defaultsChosen(selected, options)
  if (!Array.isArray(current)) return defaults ? {} : { gitPatIds: gitPatIdsPayload(selected, options) }
  if (defaults) return { gitPatIds: ['*'] }
  const chosen = options.map(pat => pat.id).filter(id => selected.includes(id))
  const kept = gitPatSelectionFor(current, options)
  const same = chosen.length === kept.length && chosen.every(id => kept.includes(id))
  return same ? {} : { gitPatIds: chosen }
}

export function authLabel(authMode) {
  return authMode === 'ApiKey' ? 'API key' : authMode === 'Auto' ? 'Auto (legacy)' : authMode || ''
}

function apiKeyReadiness(provider, statusKey, status) {
  const ready = !!status[statusKey]
  return ready
    ? { ready, text: `${provider} API key is stored for API billing.` }
    : { ready, text: `No ${provider} API key is stored. Add it in Credentials before starting this session.` }
}

export function credentialReadiness(agent, authMode, mode, status = {}, openClawApiKeySource = null) {
  if (authMode === 'Auto') return { ready: true, text: 'Legacy automatic credential selection is preserved until you choose a billing source.' }
  // The key is optional: many self-hosted servers run without one.
  if (authMode === 'CustomEndpoint') {
    return status.customEndpointApiKey
      ? { ready: true, text: 'Custom endpoint API key is stored. The first turn can take minutes while the server reads the prompt.' }
      : { ready: true, text: 'No custom endpoint API key is stored, which is fine for a server without auth. The first turn can take minutes.' }
  }
  if (authMode === 'Subscription') {
    const ready = !!status[
      agent === 'Codex' ? 'codexSubscription'
        : agent === 'Cursor' ? 'cursorSubscription'
          : agent === 'OpenClaw' ? 'openclawSubscription'
            : agent === 'OpenCode' ? 'opencodeSubscription'
            : 'claudeSubscription'
    ]
    if (ready) return { ready, text: `${agent} subscription login is stored.` }
    if (mode === 'Interactive') return { ready, text: `No stored ${agent} subscription login. You can start now and sign in inside the session.` }
    return { ready, text: `No ${agent} subscription login is stored. Sign in during an Interactive session before starting this automation.` }
  }
  if (agent === 'OpenClaw') {
    const source = openClawApiKeySource || 'Anthropic'
    if (source === 'OpenAI') return apiKeyReadiness('OpenAI', 'openAiApiKey', status)
    if (source === 'Cursor') return apiKeyReadiness('Cursor', 'cursorApiKey', status)
    return apiKeyReadiness('Anthropic', 'anthropicApiKey', status)
  }
  if (agent === 'OpenCode') return apiKeyReadiness('OpenCode', 'openCodeApiKey', status)
  const provider = agent === 'Codex' ? 'OpenAI' : agent === 'Cursor' ? 'Cursor' : 'Anthropic'
  const statusKey = agent === 'Codex' ? 'openAiApiKey' : agent === 'Cursor' ? 'cursorApiKey' : 'anthropicApiKey'
  return apiKeyReadiness(provider, statusKey, status)
}

/** Org catalog entries use this owner id (see backend McpServerRecord.OrgOwner). */
export const ORG_MCP_OWNER = '__org__'

/** Badge label for a catalog MCP row in session pickers. */
export function mcpBadgeLabel(server) {
  if (server?.owner === ORG_MCP_OWNER) return 'Org'
  if (server?.mine) return 'Mine'
  return 'Shared'
}

/** Derive a stable MCP name from an API/spec URL hostname (e.g. api.example.com → api-example-com). */
export function ephemeralNameFromUrl(url) {
  try {
    const host = new URL(String(url || '').trim()).hostname
    const name = host.replace(/[^a-zA-Z0-9]+/g, '-').replace(/^-+|-+$/g, '').toLowerCase()
    return name || 'api'
  } catch {
    return 'api'
  }
}

/** Build Create/UpdateSession ephemeralApiSources from the Advanced URL paste fields. */
export function buildEphemeralApiSources({ url, name, saveToLibrary }) {
  const specUrl = String(url || '').trim()
  if (!specUrl) return []
  const resolved = String(name || '').trim() || ephemeralNameFromUrl(specUrl)
  return [{
    name: resolved,
    specUrl,
    specType: 'auto',
    saveToLibrary: !!saveToLibrary
  }]
}
