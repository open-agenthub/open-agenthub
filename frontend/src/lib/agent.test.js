import { describe, expect, it } from 'vitest'
import {
  agentOptions, agentPayload, authOptions, credentialReadiness, defaultAgentForm, defaultPolicy,
  filterAgentOptions, gitPatIdsChange, gitPatIdsPayload, gitPatOptions, gitPatSelectionFor,
  needsOpenClawApiKeySource, openClawApiKeySourceOptions, policyFromForm, policyPayload
} from './agent.js'

/// A session's git token selection (docs/credential-scopes.md): null = every stored token,
/// including later ones; a list = exactly these; '*' = back to all on an update.
describe('git PAT selection helpers', () => {
  const two = [{ id: 'a', kind: 'gitlab', host: 'gitlab.example.com' }, { id: 'b', kind: 'github', host: 'github.com' }]

  it('reads the options from the credential status and ticks a session\'s selection', () => {
    expect(gitPatOptions({ gitPats: two })).toEqual(two)
    expect(gitPatOptions({})).toEqual([])
    expect(gitPatOptions(null)).toEqual([])
    expect(gitPatSelectionFor(null, two)).toEqual(['a', 'b'])
    expect(gitPatSelectionFor(['b', 'gone'], two)).toEqual(['b'])
    expect(gitPatSelectionFor([], two)).toEqual([])
  })

  it('sends null for "every token" on create and the ids otherwise', () => {
    expect(gitPatIdsPayload(['a', 'b'], two)).toBeNull()
    expect(gitPatIdsPayload(['b'], two)).toEqual(['b'])
    expect(gitPatIdsPayload([], two)).toEqual([])
    // Nothing stored: nothing to choose, so nothing is sent.
    expect(gitPatIdsPayload([], [])).toBeNull()
  })

  it('sends an edit only when the selection changed, and the wildcard to go back to all', () => {
    expect(gitPatIdsChange(['a', 'b'], two, null)).toEqual({})
    expect(gitPatIdsChange(['a'], two, null)).toEqual({ gitPatIds: ['a'] })
    expect(gitPatIdsChange(['a'], two, ['a'])).toEqual({})
    expect(gitPatIdsChange(['a', 'b'], two, ['a'])).toEqual({ gitPatIds: ['*'] })
    expect(gitPatIdsChange([], two, ['a'])).toEqual({ gitPatIds: [] })
    expect(gitPatIdsChange([], two, [])).toEqual({})
    expect(gitPatIdsChange([], [], ['a'])).toEqual({})
  })
})

describe('agent session helpers', () => {
  it('defaults new sessions to Claude subscription', () => {
    expect(defaultAgentForm()).toMatchObject({ agent: 'Claude', authMode: 'Subscription' })
  })

  it('includes OpenClaw and OpenCode in agent options', () => {
    expect(agentOptions.map(option => option.value)).toEqual(['Claude', 'Codex', 'Cursor', 'OpenClaw', 'OpenCode'])
  })

  it('filterAgentOptions keeps every agent when the allowlist is empty or missing', () => {
    expect(filterAgentOptions([])).toEqual(agentOptions)
    expect(filterAgentOptions(null)).toEqual(agentOptions)
    expect(filterAgentOptions(undefined)).toEqual(agentOptions)
  })

  it('filterAgentOptions keeps only allowlisted agents in catalog order', () => {
    expect(filterAgentOptions(['OpenClaw', 'Claude']).map(option => option.value)).toEqual(['Claude', 'OpenClaw'])
  })

  it('filterAgentOptions can keep a current agent that is no longer allowlisted', () => {
    expect(filterAgentOptions(['Claude'], { include: 'OpenClaw' }).map(option => option.value))
      .toEqual(['Claude', 'OpenClaw'])
  })

  it('offers both public agents and auth modes without Auto', () => {
    expect(agentOptions.map(option => option.value)).toEqual(['Claude', 'Codex', 'Cursor', 'OpenClaw', 'OpenCode'])
    expect(authOptions('Codex').map(option => option.value)).toEqual(['Subscription', 'ApiKey'])
    expect(authOptions('Cursor').map(option => option.value)).toEqual(['Subscription', 'ApiKey'])
    expect(authOptions('OpenClaw').map(option => option.value)).toEqual(['Subscription', 'ApiKey'])
  })

  it('offers Auto only for an existing migrated Claude Auto session', () => {
    expect(authOptions('Claude', 'Auto').map(option => option.value)).toEqual(['Auto', 'Subscription', 'ApiKey'])
    expect(authOptions('Codex', 'Auto').map(option => option.value)).not.toContain('Auto')
    expect(authOptions('Cursor', 'Auto').map(option => option.value)).not.toContain('Auto')
    expect(authOptions('OpenClaw', 'Auto').map(option => option.value)).not.toContain('Auto')
  })

  it('exposes Anthropic/OpenAI/Cursor API key sources for OpenClaw', () => {
    expect(openClawApiKeySourceOptions.map(option => option.value)).toEqual(['Anthropic', 'OpenAI', 'Cursor'])
    expect(needsOpenClawApiKeySource('OpenClaw', 'ApiKey')).toBe(true)
    expect(needsOpenClawApiKeySource('OpenClaw', 'Subscription')).toBe(false)
    expect(needsOpenClawApiKeySource('Claude', 'ApiKey')).toBe(false)
  })

  it('includes openClawApiKeySource in payloads only for OpenClaw ApiKey', () => {
    expect(agentPayload({ agent: 'OpenClaw', authMode: 'ApiKey', openClawApiKeySource: 'OpenAI' }))
      .toEqual({ agent: 'OpenClaw', authMode: 'ApiKey', openClawApiKeySource: 'OpenAI' })
    expect(agentPayload({ agent: 'OpenClaw', authMode: 'Subscription', openClawApiKeySource: 'Anthropic' }))
      .toEqual({ agent: 'OpenClaw', authMode: 'Subscription' })
    expect(agentPayload({ agent: 'Claude', authMode: 'ApiKey', openClawApiKeySource: 'Anthropic' }))
      .toEqual({ agent: 'Claude', authMode: 'ApiKey' })
  })

  it('preserves openClawApiKeySource when loading an existing form', () => {
    expect(defaultAgentForm({
      agent: 'OpenClaw', authMode: 'ApiKey', openClawApiKeySource: 'Cursor'
    })).toMatchObject({
      agent: 'OpenClaw', authMode: 'ApiKey', openClawApiKeySource: 'Cursor'
    })
  })

  it('uses provider-aware Codex command defaults', () => {
    expect(defaultPolicy('Codex').allowedCommands).toEqual(expect.arrayContaining(['git status', 'npm test', 'dotnet test']))
  })

  it('uses Cursor permission token defaults and empty commands', () => {
    expect(defaultPolicy('Cursor')).toMatchObject({
      allowedTools: expect.arrayContaining(['Read(**)']),
      allowedMcpTools: [],
      allowedCommands: []
    })
    expect(defaultPolicy('Cursor').allowedTools).toEqual(
      expect.arrayContaining([expect.stringMatching(/^Shell\(/), expect.stringMatching(/^Write\(/)])
    )
  })

  it('parses newline-oriented policy, trims entries, and removes duplicates', () => {
    expect(policyPayload({
      allowedToolsRaw: 'Read\n Edit \nRead',
      allowedMcpToolsRaw: 'mcp__docs__search\r\nmcp__docs__*\nmcp__docs__search',
      allowedCommandsRaw: 'git status\n npm test\ngit status'
    })).toEqual({
      allowedTools: ['Read', 'Edit'],
      allowedMcpTools: ['mcp__docs__search', 'mcp__docs__*'],
      allowedCommands: ['git status', 'npm test']
    })
  })

  it('parses form policy without Cursor force-empty commands for untouched checks', () => {
    const form = {
      agent: 'Cursor',
      allowedToolsRaw: 'Read\nEdit',
      allowedMcpToolsRaw: '',
      allowedCommandsRaw: 'git status\nnpm test\ndotnet test'
    }
    expect(policyFromForm(form)).toEqual({
      allowedTools: ['Read', 'Edit'],
      allowedMcpTools: [],
      allowedCommands: ['git status', 'npm test', 'dotnet test']
    })
    expect(policyPayload(form)).toEqual({
      allowedTools: ['Read', 'Edit'],
      allowedMcpTools: [],
      allowedCommands: []
    })
  })

  it('preserves structured policy values without filling empty categories from legacy data', () => {
    expect(defaultAgentForm({
      agent: 'Claude', authMode: 'Auto',
      allowedTools: ['Read'],
      policy: { allowedTools: [], allowedMcpTools: ['mcp__git__*'], allowedCommands: ['git status'] }
    })).toMatchObject({
      agent: 'Claude', authMode: 'Auto',
      allowedToolsRaw: '',
      allowedMcpToolsRaw: 'mcp__git__*',
      allowedCommandsRaw: 'git status'
    })
  })

  it('uses legacy allowed tools only when structured policy is absent', () => {
    expect(defaultAgentForm({ agent: 'Claude', authMode: 'Auto', allowedTools: ['Read'], policy: null }))
      .toMatchObject({ allowedToolsRaw: 'Read', allowedMcpToolsRaw: '', allowedCommandsRaw: '' })
  })

  it('preserves an explicitly empty structured policy as default deny', () => {
    expect(defaultAgentForm({
      agent: 'Codex', authMode: 'ApiKey',
      policy: { allowedTools: [], allowedMcpTools: [], allowedCommands: [] }
    })).toMatchObject({
      allowedToolsRaw: '',
      allowedMcpToolsRaw: '',
      allowedCommandsRaw: ''
    })
  })

  it.each([
    ['Claude', 'Subscription', 'Interactive', {}, null, false, 'start now and sign in'],
    ['Claude', 'Subscription', 'Autonomous', { claudeSubscription: true }, null, true, 'subscription login is stored'],
    ['Codex', 'Subscription', 'Scheduled', {}, null, false, 'Interactive session'],
    ['Cursor', 'Subscription', 'Interactive', {}, null, false, 'start now and sign in'],
    ['Cursor', 'Subscription', 'Autonomous', { cursorSubscription: true }, null, true, 'subscription login is stored'],
    ['OpenClaw', 'Subscription', 'Interactive', {}, null, false, 'start now and sign in'],
    ['OpenClaw', 'Subscription', 'Autonomous', { openclawSubscription: true }, null, true, 'subscription login is stored'],
    ['OpenClaw', 'Subscription', 'Scheduled', {}, null, false, 'Interactive session'],
    ['Claude', 'ApiKey', 'Autonomous', { anthropicApiKey: true }, null, true, 'Anthropic API key is stored'],
    ['Codex', 'ApiKey', 'Interactive', {}, null, false, 'Add it in Credentials'],
    ['Cursor', 'ApiKey', 'Autonomous', { cursorApiKey: true }, null, true, 'Cursor API key is stored'],
    ['Cursor', 'ApiKey', 'Interactive', {}, null, false, 'Add it in Credentials'],
    ['OpenClaw', 'ApiKey', 'Autonomous', { anthropicApiKey: true }, 'Anthropic', true, 'Anthropic API key is stored'],
    ['OpenClaw', 'ApiKey', 'Autonomous', { openAiApiKey: true }, 'OpenAI', true, 'OpenAI API key is stored'],
    ['OpenClaw', 'ApiKey', 'Interactive', { cursorApiKey: true }, 'Cursor', true, 'Cursor API key is stored'],
    ['OpenClaw', 'ApiKey', 'Autonomous', {}, 'OpenAI', false, 'Add it in Credentials']
  ])('reports credential readiness for %s %s %s', (agent, authMode, mode, status, source, ready, text) => {
    expect(credentialReadiness(agent, authMode, mode, status, source)).toMatchObject({ ready })
    expect(credentialReadiness(agent, authMode, mode, status, source).text).toContain(text)
  })

  it('uses Claude-like policy defaults for OpenClaw', () => {
    expect(defaultPolicy('OpenClaw')).toEqual(defaultPolicy('Claude'))
  })

  it('uses Codex-shaped policy defaults for OpenCode', () => {
    expect(defaultPolicy('OpenCode')).toEqual({
      allowedTools: ['Read', 'Edit', 'Glob', 'Grep'],
      allowedMcpTools: [],
      allowedCommands: ['git status', 'npm test']
    })
    expect(policyPayload({ agent: 'OpenCode', allowedToolsRaw: 'Read', allowedMcpToolsRaw: '', allowedCommandsRaw: 'git status' }))
      .toEqual({ allowedTools: ['Read'], allowedMcpTools: [], allowedCommands: ['git status'] })
  })

  it('checks the OpenCode key and login for OpenCode sessions', () => {
    expect(credentialReadiness('OpenCode', 'ApiKey', 'Autonomous', { openCodeApiKey: true }).ready).toBe(true)
    expect(credentialReadiness('OpenCode', 'ApiKey', 'Autonomous', { anthropicApiKey: true }))
      .toMatchObject({ ready: false, text: expect.stringContaining('No OpenCode API key') })
    expect(credentialReadiness('OpenCode', 'Subscription', 'Autonomous', { opencodeSubscription: true }).ready).toBe(true)
    expect(credentialReadiness('OpenCode', 'Subscription', 'Interactive', {}).text).toContain('sign in inside the session')
    expect(agentPayload({ agent: 'OpenCode', authMode: 'ApiKey' })).toEqual({ agent: 'OpenCode', authMode: 'ApiKey' })
  })
})
