// @vitest-environment happy-dom
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { flushPromises, mount } from '@vue/test-utils'
import FilesPane from './FilesPane.vue'

const mocks = vi.hoisted(() => ({
  api: {
    reserveSessionFile: vi.fn(),
    uploadSessionFile: vi.fn(),
    completeSessionFile: vi.fn(),
    deleteSessionFile: vi.fn().mockResolvedValue({})
  },
  content: vi.fn(),
  sharedContent: vi.fn()
}))

vi.mock('../api.js', () => ({
  api: mocks.api,
  getSessionFileContent: mocks.content,
  getSharedSessionFileContent: mocks.sharedContent
}))

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

  describe('download', () => {
    const office = 'application/vnd.openxmlformats-officedocument.presentationml.presentation'
    const files = [
      { id: 'img', name: 'shot.png', mimeType: 'image/png', size: 12, state: 'Ready' },
      { id: 'pdf', name: 'cv.pdf', mimeType: 'application/pdf', size: 12, state: 'Ready' },
      { id: 'md', name: 'notes.md', mimeType: 'text/markdown', size: 12, state: 'Ready' },
      { id: 'txt', name: 'big.log', mimeType: 'text/plain', size: 3 * 1024 * 1024, state: 'Ready' },
      { id: 'deck', name: 'deck.pptx', mimeType: office, size: 12, state: 'Ready', previewState: 'Ready', previewFileId: 'deck-pdf' },
      { id: 'html', name: 'page.html', mimeType: 'text/html', size: 12, state: 'Ready' },
      { id: 'gone', name: 'old.png', mimeType: 'image/png', size: 12, state: 'Expired' }
    ]
    let saved

    beforeEach(() => {
      vi.clearAllMocks()
      saved = []
      URL.createObjectURL = vi.fn(blob => { saved.push({ blob }); return `blob:save-${saved.length}` })
      URL.revokeObjectURL = vi.fn()
      // happy-dom would try to navigate; record what the browser would have been asked to save.
      vi.spyOn(HTMLAnchorElement.prototype, 'click').mockImplementation(function () {
        saved.at(-1).name = this.download
      })
    })
    afterEach(() => { vi.restoreAllMocks() })

    const mountPane = (props = {}) => mount(FilesPane, {
      props: { sessionId: 's1', files, capabilities: {}, ...props },
      global: { stubs: { FilePreview: true } }
    })

    it('offers a download for every listed file whatever its preview kind', () => {
      const wrapper = mountPane()
      for (const id of ['img', 'pdf', 'md', 'txt', 'deck', 'html']) {
        expect(wrapper.find(`[data-file-download="${id}"]`).exists()).toBe(true)
      }
      // Its content is gone; the backend answers 410 for it.
      expect(wrapper.find('[data-file-download="gone"]').exists()).toBe(false)
    })

    it('saves a text file whole and under its own name, not the 1 MiB the preview shows', async () => {
      const original = new Blob([new Uint8Array(3 * 1024 * 1024).fill(65)], { type: 'text/plain' })
      mocks.content.mockResolvedValue(original)
      const wrapper = mountPane()
      await wrapper.get('[data-file-download="txt"]').trigger('click')
      await flushPromises()

      expect(mocks.content).toHaveBeenCalledWith('s1', 'txt')
      expect(saved).toHaveLength(1)
      expect(saved[0].name).toBe('big.log')
      expect(saved[0].blob.size).toBe(original.size)
      expect(wrapper.emitted('select')).toBeUndefined()
    })

    it('fetches the Office document itself, not the PDF rendered for its preview', async () => {
      mocks.content.mockResolvedValue(new Blob(['PK\u0003\u0004deck'], { type: office }))
      const wrapper = mountPane({ selectedId: 'deck' })
      await wrapper.get('[data-files-download]').trigger('click')
      await flushPromises()

      expect(mocks.content).toHaveBeenCalledTimes(1)
      expect(mocks.content).toHaveBeenCalledWith('s1', 'deck')
      expect(saved[0].name).toBe('deck.pptx')
      expect(await saved[0].blob.text()).toBe('PK\u0003\u0004deck')
    })

    // A blob: URL is same-origin and served with the blob's type. If the saved link were ever
    // navigated to instead of downloaded, a text/html label would run the upload on our origin.
    it('never hands the browser a download_only type under its own label', async () => {
      mocks.content.mockResolvedValue(new Blob(['<script>alert(1)<\/script>'], { type: 'text/html' }))
      const wrapper = mountPane()
      await wrapper.get('[data-file-download="html"]').trigger('click')
      await flushPromises()

      expect(saved[0].blob.type).toBe('application/octet-stream')
      expect(await saved[0].blob.text()).toBe('<script>alert(1)<\/script>')
      expect(saved[0].name).toBe('page.html')
    })

    it('downloads through the share token for a viewer of a shared session', async () => {
      mocks.sharedContent.mockResolvedValue(new Blob(['png'], { type: 'image/png' }))
      const wrapper = mountPane({ sharedToken: 'tok', canWrite: false, selectedId: 'img' })
      await wrapper.get('[data-files-download]').trigger('click')
      await flushPromises()

      expect(mocks.sharedContent).toHaveBeenCalledWith('tok', 'img')
      expect(mocks.content).not.toHaveBeenCalled()
      expect(saved[0].name).toBe('shot.png')
    })

    it('says so when the content cannot be fetched and lets the user try again', async () => {
      mocks.content.mockRejectedValueOnce(new Error('403 file_access_denied'))
      const wrapper = mountPane({ selectedId: 'pdf' })
      await wrapper.get('[data-files-download]').trigger('click')
      await flushPromises()

      expect(saved).toHaveLength(0)
      expect(wrapper.get('[data-files-download-error]').text()).toContain('cv.pdf')
      expect(wrapper.get('[data-files-download]').attributes('disabled')).toBeUndefined()

      mocks.content.mockResolvedValue(new Blob(['%PDF'], { type: 'application/pdf' }))
      await wrapper.get('[data-files-download]').trigger('click')
      await flushPromises()
      expect(saved).toHaveLength(1)
      expect(wrapper.find('[data-files-download-error]').exists()).toBe(false)
    })

    it('does not start the same download twice while it is running', async () => {
      let finish
      mocks.content.mockReturnValue(new Promise(resolve => { finish = resolve }))
      const wrapper = mountPane()
      await wrapper.get('[data-file-download="img"]').trigger('click')
      await wrapper.get('[data-file-download="img"]').trigger('click')
      expect(mocks.content).toHaveBeenCalledTimes(1)
      finish(new Blob(['png']))
      await flushPromises()
      expect(saved).toHaveLength(1)
    })
  })
})
