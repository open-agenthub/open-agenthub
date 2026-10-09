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

export function needsOpenClawApiKeySource(agent, authMode) {
  return agent === 'OpenClaw' && authMode === 'ApiKey'
}

export function authOptions(agent, legacyMode) {
  const options = PUBLIC_AUTH_OPTIONS.map(option => ({ ...option }))
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
