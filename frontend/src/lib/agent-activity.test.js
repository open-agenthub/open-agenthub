import { describe, expect, it } from 'vitest'
import { parseActivityFrame } from './agent-activity.js'
describe('activity control frame', () => {
  it('accepts only a complete known control frame and leaves PTY content alone', () => {
    expect(parseActivityFrame(JSON.stringify({ type: 'agenthub', subtype: 'activity', state: 'working', source: 'hooks' }))).toEqual({ state: 'working', source: 'hooks' })
    expect(parseActivityFrame(JSON.stringify({ type: 'agenthub', subtype: 'activity', state: 'waiting', source: 'hooks', reason: 'answer' })))
      .toEqual({ state: 'waiting', source: 'hooks', reason: 'answer' })
    expect(parseActivityFrame(JSON.stringify({ type: 'agenthub', subtype: 'activity', state: 'waiting', source: 'hooks', reason: 'other' })))
      .toEqual({ state: 'waiting', source: 'hooks' })
    expect(parseActivityFrame(JSON.stringify({ type: 'agenthub', subtype: 'activity', state: 'unknown', source: 'hooks', ready: true })))
      .toEqual({ state: 'unknown', source: 'hooks', ready: true })
    expect(parseActivityFrame(JSON.stringify({ type: 'agenthub', subtype: 'activity', state: 'idle', source: 'output', ready: 'yes' })))
      .toEqual({ state: 'idle', source: 'output' })
    for (const data of ['ordinary terminal text', '{"type":"tool"}', '{"type":"agenthub","subtype":"activity","state":"bogus"}', new Uint8Array([1])]) expect(parseActivityFrame(data)).toBeNull()
  })
})

// Malformed control data must keep its terminal path instead of hiding arbitrary output.
import { parsePersistenceFrame } from './agent-activity.js'
it('validates the reconnect-safe persistence status', () => {
  expect(parsePersistenceFrame('{"type":"agenthub","subtype":"persistence","withheld":true}')).toEqual({ withheld: true })
  expect(parsePersistenceFrame('{"type":"agenthub","subtype":"persistence","withheld":false}')).toEqual({ withheld: false })
  expect(parsePersistenceFrame('{"type":"agenthub","subtype":"persistence","withheld":"yes"}')).toBeNull()
})

import { parseHandoverFrame } from './agent-activity.js'
it('reads an undelivered context handover and its clearing, nothing else (#336)', () => {
  expect(parseHandoverFrame(JSON.stringify({ type: 'agenthub', subtype: 'handover', failed: true, reason: 'context_clear_input_not_ready', next: 'it is sent at the next safe point' })))
    .toEqual({ failed: true, reason: 'context_clear_input_not_ready', next: 'it is sent at the next safe point' })
  expect(parseHandoverFrame('{"type":"agenthub","subtype":"handover","failed":false}')).toEqual({ failed: false })
  expect(parseHandoverFrame(JSON.stringify({ type: 'agenthub', subtype: 'handover', failed: true, reason: 'x'.repeat(500) })).reason).toHaveLength(200)
  for (const data of ['{"type":"agenthub","subtype":"handover","failed":"yes"}', '{"type":"agenthub","subtype":"persistence","withheld":true}', 'plain text'])
    expect(parseHandoverFrame(data)).toBeNull()
})
