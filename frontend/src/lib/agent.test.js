import { describe, expect, it } from 'vitest'
import { agentOptions, authOptions, credentialReadiness, defaultAgentForm, defaultPolicy, policyPayload } from './agent.js'

describe('agent session helpers', () => {
  it('defaults new sessions to Claude subscription', () => {
    expect(defaultAgentForm()).toMatchObject({ agent: 'Claude', authMode: 'Subscription' })
  })

  it('offers both public agents and auth modes without Auto', () => {
    expect(agentOptions.map(option => option.value)).toEqual(['Claude', 'Codex'])
    expect(authOptions('Codex').map(option => option.value)).toEqual(['Subscription', 'ApiKey'])
  })

  it('offers Auto only for an existing migrated Claude Auto session', () => {
    expect(authOptions('Claude', 'Auto').map(option => option.value)).toEqual(['Auto', 'Subscription', 'ApiKey'])
    expect(authOptions('Codex', 'Auto').map(option => option.value)).not.toContain('Auto')
  })

  it('uses provider-aware Codex command defaults', () => {
    expect(defaultPolicy('Codex').allowedCommands).toEqual(expect.arrayContaining(['git status', 'npm test', 'dotnet test']))
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
    ['Claude', 'Subscription', 'Interactive', {}, false, 'start now and sign in'],
    ['Claude', 'Subscription', 'Autonomous', { claudeSubscription: true }, true, 'subscription login is stored'],
    ['Codex', 'Subscription', 'Scheduled', {}, false, 'Interactive session'],
    ['Claude', 'ApiKey', 'Autonomous', { anthropicApiKey: true }, true, 'Anthropic API key is stored'],
    ['Codex', 'ApiKey', 'Interactive', {}, false, 'Add it in Credentials']
  ])('reports credential readiness for %s %s %s', (agent, authMode, mode, status, ready, text) => {
    expect(credentialReadiness(agent, authMode, mode, status)).toMatchObject({ ready })
    expect(credentialReadiness(agent, authMode, mode, status).text).toContain(text)
  })
})
