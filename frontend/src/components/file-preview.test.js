// @vitest-environment happy-dom
import { beforeEach, describe, expect, it, vi } from 'vitest'
import { flushPromises, mount } from '@vue/test-utils'
import FilePreview from './FilePreview.vue'

const mocks = vi.hoisted(() => ({ content: vi.fn(), stream: vi.fn() }))
vi.mock('../api.js', () => ({
  api: { issueSessionFileStream: mocks.stream },
  getSessionFileContent: mocks.content,
  getSharedSessionFileContent: mocks.content,
  sharedSessionFileContentUrl: (token, fileId) => `/api/shared/${token}/files/${fileId}/content`
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

  // Chrome will not instantiate its PDF viewer inside a sandboxed frame under any token
  // combination, so a sandbox attribute here shows the grey "blocked content" placeholder
  // instead of the document. Tightening this back up means losing the preview entirely — the
  // application/pdf label on the blob is what keeps an upload from running on our origin.
  it('frames a PDF unsandboxed, from a blob relabelled application/pdf', async () => {
    mocks.content.mockResolvedValue(new Blob(['<script>alert(1)<\/script>'], { type: 'text/html' }))
    const wrapper = mount(FilePreview, { props: {
      sessionId: 's1', file: { id: 'f1', name: 'cv.pdf', mimeType: 'application/pdf', size: 9, state: 'Ready' }, capabilities: {}
    } })
    await flushPromises()
    const frame = wrapper.get('[data-file-preview="pdf"]')
    expect(frame.attributes('sandbox')).toBeUndefined()
    expect(frame.attributes('src')).toBe('blob:preview')
    expect(URL.createObjectURL).toHaveBeenCalledTimes(1)
    expect(URL.createObjectURL.mock.calls[0][0].type).toBe('application/pdf')
  })

  it('relabels the Office derivative the same way', async () => {
    mocks.content.mockResolvedValue(new Blob(['%PDF-1.4'], { type: 'application/octet-stream' }))
    const wrapper = mount(FilePreview, { props: {
      sessionId: 's1',
      file: {
        id: 'f1', name: 'deck.pptx', size: 9, state: 'Ready',
        mimeType: 'application/vnd.openxmlformats-officedocument.presentationml.presentation',
        previewState: 'Ready', previewFileId: 'f1-pdf'
      },
      capabilities: {}
    } })
    await flushPromises()
    expect(mocks.content).toHaveBeenCalledWith('s1', 'f1-pdf')
    expect(wrapper.get('[data-file-preview="pdf"]').attributes('sandbox')).toBeUndefined()
    expect(URL.createObjectURL.mock.calls[0][0].type).toBe('application/pdf')
  })

  it('plays a video from a stream ticket instead of downloading it into a blob', async () => {
    mocks.stream.mockResolvedValueOnce({ url: '/api/sessions/s1/files/v1/stream?ticket=one' })
    const wrapper = mount(FilePreview, { props: {
      sessionId: 's1', file: { id: 'v1', name: 'demo.mp4', mimeType: 'video/mp4', size: 9, state: 'Ready' }, capabilities: {}
    } })
    await flushPromises()
    const video = wrapper.get('[data-file-preview="video"]')
    expect(video.attributes('src')).toBe('/api/sessions/s1/files/v1/stream?ticket=one')
    expect(video.attributes('controls')).toBeDefined()
    expect(mocks.stream).toHaveBeenCalledWith('s1', 'v1')
    expect(mocks.content).not.toHaveBeenCalled()
  })

  it('asks for one fresh ticket when playback fails, then reports the error', async () => {
    mocks.stream
      .mockResolvedValueOnce({ url: '/stream?ticket=one' })
      .mockResolvedValueOnce({ url: '/stream?ticket=two' })
    const wrapper = mount(FilePreview, { props: {
      sessionId: 's1', file: { id: 'v1', name: 'demo.webm', mimeType: 'video/webm', size: 9, state: 'Ready' }, capabilities: {}
    } })
    await flushPromises()
    await wrapper.get('video').trigger('error')
    await flushPromises()
    expect(wrapper.get('video').attributes('src')).toBe('/stream?ticket=two')
    await wrapper.get('video').trigger('error')
    await flushPromises()
    expect(wrapper.find('video').exists()).toBe(false)
    expect(wrapper.text()).toContain('This video could not be played.')
    expect(mocks.stream).toHaveBeenCalledTimes(2)
  })

  it('renews the ticket again once playback has resumed, so a long video outlives several', async () => {
    mocks.stream
      .mockResolvedValueOnce({ url: '/stream?ticket=one' })
      .mockResolvedValueOnce({ url: '/stream?ticket=two' })
      .mockResolvedValueOnce({ url: '/stream?ticket=three' })
    const wrapper = mount(FilePreview, { props: {
      sessionId: 's1', file: { id: 'v1', name: 'demo.mp4', mimeType: 'video/mp4', size: 9, state: 'Ready' }, capabilities: {}
    } })
    await flushPromises()
    await wrapper.get('video').trigger('error')
    await flushPromises()
    await wrapper.get('video').trigger('playing')
    await wrapper.get('video').trigger('error')
    await flushPromises()
    expect(wrapper.get('video').attributes('src')).toBe('/stream?ticket=three')
  })

  it('plays a shared video straight from the share link, which carries its own credential', async () => {
    const wrapper = mount(FilePreview, { props: {
      sessionId: 's1', sharedToken: 'tok',
      file: { id: 'v1', name: 'demo.mp4', mimeType: 'video/mp4', size: 9, state: 'Ready' }, capabilities: {}
    } })
    await flushPromises()
    expect(wrapper.get('video').attributes('src')).toBe('/api/shared/tok/files/v1/content')
    expect(mocks.stream).not.toHaveBeenCalled()
  })

  it('leaves an image blob at its own type', async () => {
    mocks.content.mockResolvedValue(new Blob(['png'], { type: 'image/png' }))
    const wrapper = mount(FilePreview, { props: {
      sessionId: 's1', file: { id: 'f1', name: 'shot.png', mimeType: 'image/png', size: 3, state: 'Ready' }, capabilities: {}
    } })
    await flushPromises()
    expect(wrapper.get('[data-file-preview="image"]').attributes('src')).toBe('blob:preview')
    expect(URL.createObjectURL.mock.calls[0][0].type).toBe('image/png')
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

  // The workspace repolls the file list every ~1.5s and passes down a new object each time. Keyed
  // on the object, the preview refetched on that cadence and flashed its loading state.
  it('does not refetch when the poll hands down an equal file object', async () => {
    const file = { id: 'f1', name: 'notes.md', mimeType: 'text/markdown', size: 12, state: 'Ready' }
    const wrapper = mount(FilePreview, { props: { sessionId: 's1', file, capabilities: {} } })
    await flushPromises()
    expect(mocks.content).toHaveBeenCalledTimes(1)

    await wrapper.setProps({ file: { ...file } })
    await flushPromises()
    expect(mocks.content).toHaveBeenCalledTimes(1)
    expect(wrapper.get('[data-file-preview="markdown"]').html()).toContain('Safe title')

    await wrapper.setProps({ file: { ...file, id: 'f2' } })
    await flushPromises()
    expect(mocks.content).toHaveBeenCalledTimes(2)
  })
})
