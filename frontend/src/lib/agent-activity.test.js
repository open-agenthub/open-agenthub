import { describe, expect, it } from 'vitest'
import { parseActivityFrame } from './agent-activity.js'
describe('activity control frame', () => {
  it('accepts only a complete known control frame and leaves PTY content alone', () => {
    expect(parseActivityFrame(JSON.stringify({ type: 'agenthub', subtype: 'activity', state: 'working', source: 'hooks' }))).toEqual({ state: 'working', source: 'hooks' })
    expect(parseActivityFrame(JSON.stringify({ type: 'agenthub', subtype: 'activity', state: 'waiting', source: 'hooks', reason: 'answer' })))
      .toEqual({ state: 'waiting', source: 'hooks', reason: 'answer' })
    expect(parseActivityFrame(JSON.stringify({ type: 'agenthub', subtype: 'activity', state: 'waiting', source: 'hooks', reason: 'other' })))
      .toEqual({ state: 'waiting', source: 'hooks' })
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
