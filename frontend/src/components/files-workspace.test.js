// @vitest-environment happy-dom
import { beforeEach, describe, expect, it, vi } from 'vitest'
import { flushPromises, mount } from '@vue/test-utils'
import SessionWorkspace from './SessionWorkspace.vue'

const mocks = vi.hoisted(() => ({
  capabilities: vi.fn(), list: vi.fn(), presentation: vi.fn(), dismiss: vi.fn(),
  reserve: vi.fn(), upload: vi.fn(), complete: vi.fn(), remove: vi.fn()
}))
vi.mock('../api.js', () => ({
  api: {
    sessionFileCapabilities: mocks.capabilities,
    listSessionFiles: mocks.list,
    getFilePresentation: mocks.presentation,
    setFilePresentation: mocks.dismiss,
    reserveSessionFile: mocks.reserve,
    uploadSessionFile: mocks.upload,
    completeSessionFile: mocks.complete,
    deleteSessionFile: mocks.remove
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
    mocks.reserve.mockResolvedValue({ file: { id: 'f-new' }, upload: { kind: 'proxy', url: '/upload' } })
    mocks.upload.mockResolvedValue({})
    mocks.complete.mockResolvedValue({ id: 'f-new', name: 'cv.pdf', mimeType: 'application/pdf', size: 9 })
    mocks.remove.mockResolvedValue({})
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

  // The reported failure: a file uploaded through the pane disappeared from the listing after
  // the session was restarted. Closing the pane tears the workspace shell down, and the upload
  // queue used to delete everything it held — including files that were already the session's.
  it('does not delete an upload when closing the pane tears the workspace down', async () => {
    const wrapper = mount(SessionWorkspace, {
      props: { session: { id: 's1', browser: { phase: 'Stopped' } }, canWrite: true },
      slots: { default: '<div data-terminal>terminal</div>' },
      global: { stubs: { BrowserPane: true, FilePreview: true } }
    })
    await flushPromises()
    const pdf = new File(['cv'], 'cv.pdf', { type: 'application/pdf' })
    await wrapper.get('[data-files-pane]').trigger('drop', { dataTransfer: { files: [pdf] } })
    await flushPromises()
    expect(mocks.complete).toHaveBeenCalledWith('s1', 'f-new')

    await wrapper.get('[data-files-close]').trigger('click')
    await flushPromises()
    expect(wrapper.find('[data-files-pane]').exists()).toBe(false)
    expect(mocks.remove).not.toHaveBeenCalled()
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
