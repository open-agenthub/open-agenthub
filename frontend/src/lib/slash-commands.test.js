import { describe, expect, it } from 'vitest'
import { completeCommand, leadingCommand, rankCommands, slashQuery } from './slash-commands.js'

const commands = [
  { name: 'compact', source: 'builtin', description: 'Free up context by summarizing' },
  { name: 'model', source: 'builtin', description: 'Set the AI model' },
  { name: 'add-dir', source: 'builtin', description: 'Add a new working directory' },
  { name: 'deploy-check', source: 'custom', description: 'Check the deploy' },
  { name: 'mod', source: 'skill', description: 'A skill' },
  { name: 'clear', source: 'builtin', description: 'Start a new session', effect: 'new-conversation' }
]

describe('slash command completion', () => {
  it('offers commands only for the token at the very start of the message', () => {
    expect(slashQuery('/mo')).toBe('mo')
    expect(slashQuery('/')).toBe('')
    expect(slashQuery('/model fast', 3)).toBe('model')
    expect(slashQuery('/model fast')).toBeNull()
    expect(slashQuery('please run /model')).toBeNull()
    expect(slashQuery(' /model')).toBeNull()
    expect(slashQuery('/mo', 0)).toBeNull()
  })

  it('ranks exact, prefix, word start, substring and description matches in that order', () => {
    expect(rankCommands(commands, 'mod').map(c => c.name)).toEqual(['mod', 'model'])
    expect(rankCommands(commands, 'dir').map(c => c.name)).toEqual(['add-dir'])
    expect(rankCommands(commands, 'summar').map(c => c.name)).toEqual(['compact'])
    expect(rankCommands(commands, 'check').map(c => c.name)).toEqual(['deploy-check'])
    expect(rankCommands(commands, 'zzz')).toEqual([])
    // Empty query: everything, built-ins before custom commands before skills.
    expect(rankCommands(commands, '').map(c => c.source)).toEqual(['builtin', 'builtin', 'builtin', 'builtin', 'custom', 'skill'])
  })

  it('completes the token with a trailing space and keeps the rest of the message', () => {
    expect(completeCommand('/dep', commands[3])).toEqual({ text: '/deploy-check ', caret: 14 })
    expect(completeCommand('/dep prod', commands[3])).toEqual({ text: '/deploy-check prod', caret: 14 })
  })

  it('knows the command a draft starts with', () => {
    expect(leadingCommand('/clear', commands).effect).toBe('new-conversation')
    expect(leadingCommand('/compact keep the API notes', commands).name).toBe('compact')
    expect(leadingCommand('/unknown', commands)).toBeNull()
    expect(leadingCommand('clear', commands)).toBeNull()
  })
})
