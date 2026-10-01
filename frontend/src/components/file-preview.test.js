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
})
