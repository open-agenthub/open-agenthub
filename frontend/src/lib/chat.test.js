import { describe, expect, it } from 'vitest'
import { createChatLog } from './chat.js'

const line = value => JSON.stringify(value) + '\n'

describe('createChatLog', () => {
  it('captures the model from the init event and skips unparseable lines', () => {
    const log = createChatLog()
    log.feed('x"garbage{\n' + line({ type: 'system', subtype: 'init', model: 'claude-x' }))
    expect(log.model).toBe('claude-x')
    expect(log.items).toEqual([])
  })

  it('renders echoed user input as a user bubble and marks the turn busy', () => {
    const log = createChatLog()
    log.feed(line({
      type: 'user', agenthub_echo: true,
      message: { role: 'user', content: [{ type: 'text', text: 'hello' }] }
    }))
    expect(log.items).toEqual([{ kind: 'user', text: 'hello' }])
    expect(log.busy).toBe(true)
  })

  it('merges assistant content blocks of one message into one item', () => {
    const log = createChatLog()
    const message = id => ({ type: 'assistant', message: { id, role: 'assistant', content: [] } })
    const withBlock = (event, block) => ({ ...event, message: { ...event.message, content: [block] } })
    log.feed(line(withBlock(message('m1'), { type: 'thinking', thinking: 'hm' })))
    log.feed(line(withBlock(message('m1'), { type: 'text', text: 'answer' })))
    log.feed(line(withBlock(message('m2'), { type: 'text', text: 'second message' })))
    expect(log.items).toHaveLength(2)
    expect(log.items[0].blocks).toEqual([{ type: 'thinking', text: 'hm' }, { type: 'text', text: 'answer' }])
    expect(log.items[1].blocks).toEqual([{ type: 'text', text: 'second message' }])
  })

  it('attaches tool results to their tool_use block instead of a user bubble', () => {
    const log = createChatLog()
    log.feed(line({
      type: 'assistant',
      message: { id: 'm1', content: [{ type: 'tool_use', id: 't1', name: 'Bash', input: { command: 'ls' } }] }
    }))
    log.feed(line({
      type: 'user',
      message: { role: 'user', content: [{ type: 'tool_result', tool_use_id: 't1', content: 'file.txt', is_error: false }] }
    }))
    expect(log.items).toHaveLength(1)
    const tool = log.items[0].blocks[0]
    expect(tool).toMatchObject({ type: 'tool', name: 'Bash', result: 'file.txt', isError: false })
  })

  it('streams deltas into drafts and clears them when the block finalizes', () => {
    const log = createChatLog()
    log.feed(line({ type: 'stream_event', event: { type: 'content_block_start', index: 0, content_block: { type: 'text', text: '' } } }))
    log.feed(line({ type: 'stream_event', event: { type: 'content_block_delta', index: 0, delta: { type: 'text_delta', text: 'par' } } }))
    log.feed(line({ type: 'stream_event', event: { type: 'content_block_delta', index: 0, delta: { type: 'text_delta', text: 'tial' } } }))
    expect(log.drafts).toEqual([{ index: 0, type: 'text', text: 'partial' }])
    expect(log.busy).toBe(true)
    log.feed(line({ type: 'assistant', message: { id: 'm1', content: [{ type: 'text', text: 'partial done' }] } }))
    expect(log.drafts).toEqual([])
  })

  it('ends the busy state on result and exit events', () => {
    const log = createChatLog()
    log.feed(line({ type: 'user', agenthub_echo: true, message: { content: [{ type: 'text', text: 'go' }] } }))
    log.feed(line({ type: 'result', is_error: false, total_cost_usd: 0.01, duration_api_ms: 1200 }))
    expect(log.busy).toBe(false)
    expect(log.lastResult).toEqual({ isError: false, costUsd: 0.01, durationMs: 1200 })
    log.feed(line({ type: 'agenthub', subtype: 'exit', code: 0, signal: null }))
    expect(log.items.at(-1)).toEqual({ kind: 'exit', code: 0, text: 'Session ended (code 0).' })
  })

  it('merges consecutive stderr chunks into one note', () => {
    const log = createChatLog()
    log.feed(line({ type: 'agenthub', subtype: 'stderr', text: 'boom ' }))
    log.feed(line({ type: 'agenthub', subtype: 'stderr', text: 'bang' }))
    expect(log.items).toEqual([{ kind: 'stderr', text: 'boom bang' }])
  })
})
