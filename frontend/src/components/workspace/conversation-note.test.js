// @vitest-environment happy-dom
import { describe, expect, it, vi } from 'vitest'
import { mount } from '@vue/test-utils'
vi.mock('../../lib/mermaid.js', () => ({ renderMermaidBlocks: vi.fn() }))
import ConversationTimeline from './ConversationTimeline.vue'
import { toTranscriptItems } from '../../lib/transcript.js'

describe('conversation notes (#515)', () => {
  it('shows a model switch as a short line of the record, not as a turn of the person', () => {
    const items = toTranscriptItems({ source: 'native', entries: [
      { role: 'user', text: 'Remember the word PAPAYA42.' },
      { role: 'note', text: 'Model switched to Sonnet 5.5', at: '2026-11-07T13:56:06.595Z' },
      { role: 'user', text: 'Which word?' }
    ] })
    const wrapper = mount(ConversationTimeline, { props: { items } })

    const note = wrapper.get('[data-conversation-note]')
    expect(note.text()).toContain('Model switched to Sonnet 5.5')
    expect(note.get('time').attributes('datetime')).toBe('2026-11-07T13:56:06.595Z')
    expect(wrapper.findAll('[data-workspace-role="user"]').map(turn => turn.text()))
      .toEqual([expect.stringContaining('PAPAYA42'), expect.stringContaining('Which word?')])
    expect(wrapper.find('[data-workspace-role="note"]').exists()).toBe(false)
  })
})
