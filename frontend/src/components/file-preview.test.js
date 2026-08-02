// @vitest-environment happy-dom
import { beforeEach, describe, expect, it, vi } from 'vitest'
import { flushPromises, mount } from '@vue/test-utils'
import FilePreview from './FilePreview.vue'

const mocks = vi.hoisted(() => ({ content: vi.fn() }))
vi.mock('../api.js', () => ({
  getSessionFileContent: mocks.content,
  getSharedSessionFileContent: mocks.content
}))

describe('FilePreview', () => {
  beforeEach(() => {
    vi.clearAllMocks()
    mocks.content.mockResolvedValue(new Blob(['# Safe title'], { type: 'text/markdown' }))
    URL.createObjectURL = vi.fn(() => 'blob:preview')
    URL.revokeObjectURL = vi.fn()
  })

  it('renders escaped markdown loaded through the authenticated content helper', async () => {
    const wrapper = mount(FilePreview, { props: {
      sessionId: 's1', file: { id: 'f1', name: 'notes.md', mimeType: 'text/markdown', size: 12, state: 'Ready' }, capabilities: {}
    } })
    await flushPromises()
    expect(mocks.content).toHaveBeenCalledWith('s1', 'f1')
    expect(wrapper.get('[data-file-preview="markdown"]').html()).toContain('Safe title')
  })

  it('renders HTML and SVG as download-only, never as an iframe', async () => {
    mocks.content.mockResolvedValue(new Blob(['<svg></svg>'], { type: 'image/svg+xml' }))
    const wrapper = mount(FilePreview, { props: {
      sessionId: 's1', file: { id: 'f1', name: 'x.svg', mimeType: 'image/svg+xml', size: 12, state: 'Ready' }, capabilities: {}
    } })
    await flushPromises()
    expect(wrapper.find('iframe').exists()).toBe(false)
    expect(wrapper.get('[data-preview-unsupported]').text()).toContain('Download')
    expect(wrapper.get('[data-file-download]').attributes('href')).toBe('blob:preview')
  })
})
