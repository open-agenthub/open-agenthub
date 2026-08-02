// @vitest-environment happy-dom
import { describe, expect, it, vi } from 'vitest'
import { mount } from '@vue/test-utils'
import ChatAttachments from './ChatAttachments.vue'

describe('ChatAttachments', () => {
  it('shows progress, errors, retry, and remove controls', async () => {
    const retry = vi.fn()
    const remove = vi.fn()
    const wrapper = mount(ChatAttachments, { props: {
      items: [
        { key: 'a', name: 'upload.png', state: 'uploading', progress: 40 },
        { key: 'b', name: 'bad.png', state: 'failed', error: 'Upload failed.' }
      ], retry, remove
    } })
    expect(wrapper.get('[data-attachment="a"]').text()).toContain('40%')
    expect(wrapper.get('[data-attachment-error]').text()).toContain('Upload failed')
    await wrapper.get('[data-attachment-retry]').trigger('click')
    await wrapper.get('[data-attachment-remove="a"]').trigger('click')
    expect(retry).toHaveBeenCalledWith('b')
    expect(remove).toHaveBeenCalledWith('a')
  })
})
