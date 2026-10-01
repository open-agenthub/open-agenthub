// @vitest-environment happy-dom
import { describe, expect, it, vi } from 'vitest'
import { flushPromises, mount } from '@vue/test-utils'
import FilesPane from './FilesPane.vue'

const mocks = vi.hoisted(() => ({
  api: {
    reserveSessionFile: vi.fn(),
    uploadSessionFile: vi.fn(),
    completeSessionFile: vi.fn(),
    deleteSessionFile: vi.fn().mockResolvedValue({})
  }
}))

vi.mock('../api.js', () => ({ api: mocks.api }))

describe('FilesPane', () => {
  it('selects files and exposes local close and shared dismiss separately', async () => {
    const wrapper = mount(FilesPane, { props: {
      sessionId: 's1',
      files: [{ id: 'f1', name: 'shot.png', mimeType: 'image/png', size: 12, state: 'Ready' }],
      selectedId: 'f1', capabilities: {}, canWrite: true, presented: true,
      onSelect: vi.fn(), onClose: vi.fn(), onDismiss: vi.fn()
    }, global: { stubs: { FilePreview: true } } })
    await wrapper.get('[data-file="f1"]').trigger('click')
    await wrapper.get('[data-files-close]').trigger('click')
    await wrapper.get('[data-files-dismiss]').trigger('click')
    expect(wrapper.emitted('select')[0]).toEqual(['f1'])
    expect(wrapper.emitted('close')).toHaveLength(1)
    expect(wrapper.emitted('dismiss')).toHaveLength(1)
  })

  it('uploads dropped files and asks the workspace to refresh once they land', async () => {
    mocks.api.reserveSessionFile.mockResolvedValue({
      file: { id: 'f-new' }, upload: { kind: 'proxy', url: '/upload' }
    })
    mocks.api.uploadSessionFile.mockResolvedValue({})
    mocks.api.completeSessionFile.mockResolvedValue({
      id: 'f-new', name: 'notes.txt', mimeType: 'text/plain', size: 4
    })

    const wrapper = mount(FilesPane, {
      props: { sessionId: 's1', files: [], capabilities: {}, canWrite: true },
      global: { stubs: { FilePreview: true } }
    })
    const file = new File(['note'], 'notes.txt', { type: 'text/plain' })
    await wrapper.get('[data-files-pane]').trigger('drop', { dataTransfer: { files: [file] } })
    await flushPromises()

    expect(mocks.api.reserveSessionFile).toHaveBeenCalledWith('s1', expect.objectContaining({ name: 'notes.txt' }))
    expect(mocks.api.completeSessionFile).toHaveBeenCalledWith('s1', 'f-new')
    expect(wrapper.emitted('uploaded')?.length).toBeGreaterThan(0)
  })

  it('keeps a finished upload when the pane goes away', async () => {
    mocks.api.reserveSessionFile.mockResolvedValue({
      file: { id: 'f-new' }, upload: { kind: 'proxy', url: '/upload' }
    })
    mocks.api.uploadSessionFile.mockResolvedValue({})
    mocks.api.completeSessionFile.mockResolvedValue({
      id: 'f-new', name: 'notes.txt', mimeType: 'text/plain', size: 4
    })

    mocks.api.deleteSessionFile.mockClear()

    const wrapper = mount(FilesPane, {
      props: { sessionId: 's1', files: [], capabilities: {}, canWrite: true },
      global: { stubs: { FilePreview: true } }
    })
    const file = new File(['note'], 'notes.txt', { type: 'text/plain' })
    await wrapper.get('[data-files-pane]').trigger('drop', { dataTransfer: { files: [file] } })
    await flushPromises()

    // Once the listing carries the file the upload row hands over to it, so the pane shows the
    // file once and the queue no longer holds something it does not own.
    await wrapper.setProps({ files: [{ id: 'f-new', name: 'notes.txt', mimeType: 'text/plain', size: 4 }] })
    await flushPromises()
    expect(wrapper.find('[data-files-uploads]').exists()).toBe(false)

    wrapper.unmount()
    await flushPromises()
    expect(mocks.api.deleteSessionFile).not.toHaveBeenCalled()
  })

  it('offers no upload affordance in a shared read-only view', () => {
    const wrapper = mount(FilesPane, {
      props: { sessionId: 's1', files: [], capabilities: {}, canWrite: false, sharedToken: 'tok' },
      global: { stubs: { FilePreview: true } }
    })
    expect(wrapper.find('[data-files-upload]').exists()).toBe(false)
    expect(wrapper.find('[data-files-input]').exists()).toBe(false)
  })
})
