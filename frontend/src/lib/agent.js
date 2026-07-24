export const agentOptions = [
  { value: 'Claude', label: 'Claude', hint: 'Anthropic agent runtime' },
  { value: 'Codex', label: 'Codex', hint: 'OpenAI agent runtime' }
]

const PUBLIC_AUTH_OPTIONS = [
  { value: 'Subscription', label: 'Subscription', hint: 'Use your provider plan login' },
  { value: 'ApiKey', label: 'API key', hint: 'Use provider API billing' }
]

export function authOptions(agent, legacyMode) {
  const options = PUBLIC_AUTH_OPTIONS.map(option => ({ ...option }))
  return agent === 'Claude' && legacyMode === 'Auto'
    ? [{ value: 'Auto', label: 'Auto (legacy)', hint: 'Existing migrated selection' }, ...options]
    : options
}

export function defaultPolicy(agent) {
  return agent === 'Codex'
    ? {
        allowedTools: ['Read', 'Edit'],
        allowedMcpTools: [],
        allowedCommands: ['git status', 'npm test', 'dotnet test']
      }
    : {
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
    allowedToolsRaw: policy.allowedTools.join('\n'),
    allowedMcpToolsRaw: policy.allowedMcpTools.join('\n'),
    allowedCommandsRaw: policy.allowedCommands.join('\n')
  }
}

function lines(value) {
  return [...new Set(String(value || '').split(/\r?\n/).map(item => item.trim()).filter(Boolean))]
}

export function policyPayload(form) {
  return {
    allowedTools: lines(form.allowedToolsRaw),
    allowedMcpTools: lines(form.allowedMcpToolsRaw),
    allowedCommands: lines(form.allowedCommandsRaw)
  }
}

export function authLabel(authMode) {
  return authMode === 'ApiKey' ? 'API key' : authMode === 'Auto' ? 'Auto (legacy)' : authMode || ''
}

export function credentialReadiness(agent, authMode, mode, status = {}) {
  if (authMode === 'Auto') return { ready: true, text: 'Legacy automatic credential selection is preserved until you choose a billing source.' }
  if (authMode === 'Subscription') {
    const ready = !!status[agent === 'Codex' ? 'codexSubscription' : 'claudeSubscription']
    if (ready) return { ready, text: `${agent} subscription login is stored.` }
    if (mode === 'Interactive') return { ready, text: `No stored ${agent} subscription login. You can start now and sign in inside the session.` }
    return { ready, text: `No ${agent} subscription login is stored. Sign in during an Interactive session before starting this automation.` }
  }
  const provider = agent === 'Codex' ? 'OpenAI' : 'Anthropic'
  const ready = !!status[agent === 'Codex' ? 'openAiApiKey' : 'anthropicApiKey']
  return ready
    ? { ready, text: `${provider} API key is stored for API billing.` }
    : { ready, text: `No ${provider} API key is stored. Add it in Credentials before starting this session.` }
}
