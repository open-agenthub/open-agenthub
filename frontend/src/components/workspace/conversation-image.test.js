// @vitest-environment happy-dom
import { mount, flushPromises } from '@vue/test-utils'
import { beforeEach, describe, expect, it, vi } from 'vitest'
import ConversationImage from './ConversationImage.vue'
import { getSessionFileContent, getSharedSessionFileContent } from '../../api.js'

vi.mock('../../api.js', () => ({ getSessionFileContent: vi.fn(), getSharedSessionFileContent: vi.fn() }))
beforeEach(() => {
  vi.clearAllMocks()
  URL.createObjectURL = vi.fn(() => 'blob:image')
  URL.revokeObjectURL = vi.fn()
})
const props = { sessionId: 's1', file: { id: 'f1', name: 'pasted.png' } }
describe('conversation image access', () => {
  it('uses the signed-in Files fetch and releases content on unmount', async () => {
    getSessionFileContent.mockResolvedValue(new Blob(['png'], { type: 'image/png' }))
    const view = mount(ConversationImage, { props })
    await flushPromises()
    expect(getSessionFileContent).toHaveBeenCalledWith('s1', 'f1')
    expect(view.find('img').attributes('src')).toBe('blob:image')
    view.unmount()
    expect(URL.revokeObjectURL).toHaveBeenCalledWith('blob:image')
  })
  it('uses shared Files authorization without the signed-in route', async () => {
    getSharedSessionFileContent.mockResolvedValue(new Blob(['png'], { type: 'image/png' }))
    const view = mount(ConversationImage, { props: { ...props, sharedToken: 'share' } })
    await flushPromises()
    expect(getSharedSessionFileContent).toHaveBeenCalledWith('share', 'f1')
    expect(getSessionFileContent).not.toHaveBeenCalled()
    view.unmount()
  })
  it.each([403, 404, 410])('keeps an unavailable image readable on %s', async status => {
    getSessionFileContent.mockRejectedValue(Object.assign(new Error(), { status }))
    const view = mount(ConversationImage, { props })
    await flushPromises()
    expect(view.text()).toContain('Image unavailable')
    expect(view.find('img').exists()).toBe(false)
    view.unmount()
  })
  it('does not inline active document types', async () => {
    getSessionFileContent.mockResolvedValue(new Blob(['<svg/>'], { type: 'image/svg+xml' }))
    const view = mount(ConversationImage, { props })
    await flushPromises()
    expect(URL.createObjectURL).not.toHaveBeenCalled()
    expect(view.text()).toContain('Image unavailable')
    view.unmount()
  })
})
