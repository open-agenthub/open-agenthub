// @vitest-environment happy-dom
import { beforeEach, describe, expect, it, vi } from 'vitest'
import { flushPromises, mount } from '@vue/test-utils'
import SessionWorkspace from './SessionWorkspace.vue'

const mocks = vi.hoisted(() => ({
  capabilities: vi.fn(), list: vi.fn(), presentation: vi.fn(), dismiss: vi.fn()
}))
vi.mock('../api.js', () => ({
  api: {
    sessionFileCapabilities: mocks.capabilities,
    listSessionFiles: mocks.list,
    getFilePresentation: mocks.presentation,
    setFilePresentation: mocks.dismiss
  },
  getSharedFileCapabilities: mocks.capabilities,
  listSharedSessionFiles: mocks.list,
  getSharedFilePresentation: mocks.presentation
}))

describe('files workspace', () => {
  beforeEach(() => {
    vi.clearAllMocks()
    mocks.capabilities.mockResolvedValue({ limits: { presentationPollMilliseconds: 60_000 } })
    mocks.list.mockResolvedValue([{ id: 'f1', name: 'shot.png', mimeType: 'image/png', size: 12, state: 'Ready' }])
    mocks.presentation.mockResolvedValue({ revision: 7, fileId: 'f1' })
    mocks.dismiss.mockResolvedValue({ revision: 8, fileId: null })
  })

  it('opens Files when a newer presentation revision selects a file', async () => {
    const wrapper = mount(SessionWorkspace, {
      props: { session: { id: 's1', browser: { phase: 'Stopped' } }, canWrite: true },
      slots: { default: '<div data-terminal>terminal</div>' },
      global: { stubs: { BrowserPane: true, FilePreview: true } }
    })
    await flushPromises()
    expect(wrapper.get('[data-workspace-tab="files"]').classes()).toContain('active')
    expect(wrapper.get('[data-file="f1"]').classes()).toContain('active')
  })

  it('dismisses a shared presentation without conflating local close', async () => {
    const wrapper = mount(SessionWorkspace, {
      props: { session: { id: 's1', browser: { phase: 'Stopped' } }, canWrite: true },
      slots: { default: '<div data-terminal>terminal</div>' },
      global: { stubs: { BrowserPane: true, FilePreview: true } }
    })
    await flushPromises()
    await wrapper.get('[data-files-dismiss]').trigger('click')
    expect(mocks.dismiss).toHaveBeenCalledWith('s1', null)
  })
})
