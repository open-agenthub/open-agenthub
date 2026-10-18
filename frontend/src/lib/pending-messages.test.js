import { describe, expect, it } from 'vitest'
import { fleetLabel, fleetWaitingLabel, normalizeMessage, parseFleetMessage, pendingFleetMessages, sentEntry, stillPending, trackable } from './pending-messages.js'
import { toTranscriptItems } from './transcript.js'

const user = text => ({ role: 'user', text })

describe('sent messages', () => {
  it('stay pending until the conversation holds one more user turn with their text', () => {
    const before = [user('continue'), { role: 'assistant', text: 'ok' }]
    const sent = [sentEntry('a', 'continue', before), sentEntry('b', 'Fix the\n  failing test ', before)]
    expect(sent.map(entry => entry.baseline)).toEqual([1, 0])
    expect(stillPending(sent, before).map(entry => entry.id)).toEqual(['a', 'b'])
    // The CLI took the second one; its line breaks and indentation are not what decides.
    expect(stillPending(sent, [...before, user('Fix the failing test')]).map(entry => entry.id)).toEqual(['a'])
    expect(stillPending(sent, [...before, user('Fix the failing test'), user('continue')])).toEqual([])
  })

  it('an assistant echo of the text does not count as taken', () => {
    const sent = [sentEntry('a', 'deploy now', [])]
    expect(stillPending(sent, [{ role: 'assistant', text: 'deploy now' }])).toHaveLength(1)
  })

  it('is followed only where a native conversation can show it, and never for a slash command', () => {
    expect(trackable('Fix it', 'native')).toBe(true)
    expect(trackable('Fix it', 'scrollback')).toBe(false)
    expect(trackable('  /model', 'native')).toBe(false)
    expect(trackable('  ', 'native')).toBe(false)
  })

  it('normalizes control characters and whitespace', () => {
    expect(normalizeMessage(' a\r\n\tb \x1b c ')).toBe('a b c')
  })
})

describe('fleet messages', () => {
  it('parse the header the session agent and the mod put in front of them', () => {
    expect(parseFleetMessage('[AgentHub message from agent "Code Reviewer" (abc123)]\nLook at MR 42'))
      .toEqual({ fromTitle: 'Code Reviewer', from: 'abc123', body: 'Look at MR 42' })
    expect(parseFleetMessage('[AgentHub message from outside the fleet]\nHello')).toEqual({ fromTitle: null, from: null, body: 'Hello' })
    expect(parseFleetMessage('Please read [AgentHub message from outside the fleet]')).toBeNull()
  })

  // Recorded by Claude Code 2.1.287 for a message the fleet mod submitted (real CLI, real mod).
  it('unwrap the frame Claude Code puts around a prompt a plugin submitted', () => {
    const recorded = 'The agenthub-fleet plugin sent a message:\n[AgentHub message from agent "Code Reviewer" (f00dfeed1234)]\nplease review MR 42\n\n' +
      'This is how Claude Code surfaces a prompt a plugin submits between turns — it starts this turn in the user\'s place. Address the message above.'
    expect(parseFleetMessage(recorded)).toEqual({ fromTitle: 'Code Reviewer', from: 'f00dfeed1234', body: 'please review MR 42' })
    expect(parseFleetMessage('The other plugin sent a message:\nhello')).toBeNull()
  })

  it('become their own entry in the conversation, not the person’s words (#186)', () => {
    const items = toTranscriptItems({ source: 'native', entries: [
      { role: 'user', text: '[AgentHub message from agent "Code Reviewer" (abc123)]\nLook at MR 42' },
      { role: 'user', text: 'my own words' }
    ] })
    expect(items[0]).toEqual({ role: 'fleet', label: 'Message from agent “Code Reviewer”', text: 'Look at MR 42' })
    expect(items[1].role).toBe('user')
  })

  it('are pending exactly while the hub has not recorded them delivered', () => {
    const messages = [
      { id: '1', deliveredAt: null },
      { id: '2', deliveredAt: '2026-10-18T10:00:00Z', deliveredVia: 'mod' },
      { id: '3', deliveredAt: null, deliveredVia: 'next_turn' },
      { id: '4', deliveredAt: '2026-10-18T10:00:00Z', deliveredVia: 'next_turn' }
    ]
    expect(pendingFleetMessages(messages).map(m => m.id)).toEqual(['1', '3'])
    expect(fleetWaitingLabel(messages[0])).toBe('waiting in the agent’s inbox')
    expect(fleetLabel({ from: 'abc', fromTitle: null })).toBe('Message from agent “abc”')
    expect(fleetLabel({ from: null })).toBe('Message from outside the fleet')
  })
})
