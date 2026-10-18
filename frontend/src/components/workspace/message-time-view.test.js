// @vitest-environment happy-dom
import { describe, expect, it, vi } from 'vitest'
import { mount } from '@vue/test-utils'
vi.mock('../../lib/mermaid.js', () => ({ renderMermaidBlocks: vi.fn() }))
import ConversationTimeline from './ConversationTimeline.vue'

describe('conversation timestamps (#147)', () => {
  it('puts a compact time with the full date on hover next to each message that has one', () => {
    const wrapper = mount(ConversationTimeline, { props: { items: [
      { role: 'user', label: 'User', text: 'Build it', at: '2026-10-18T16:51:41.128Z' },
      { role: 'assistant', label: 'Agent', text: 'Done', at: '2026-10-18T16:51:53.203Z' },
      { role: 'terminal', label: 'Terminal', text: 'scrollback' }
    ] } })
    const times = wrapper.findAll('[data-message-time]')
    expect(times).toHaveLength(2)
    expect(times[0].attributes('datetime')).toBe('2026-10-18T16:51:41.128Z')
    expect(times[0].attributes('title')).toMatch(/2026/)
    expect(times[0].text()).not.toBe('')
    expect(wrapper.findAll('[data-workspace-role]')[2].find('[data-message-time]').exists()).toBe(false)
  })
})
