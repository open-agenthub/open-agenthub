// @vitest-environment happy-dom
import { describe, expect, it, vi } from 'vitest'
import { mount } from '@vue/test-utils'
import FilesPane from './FilesPane.vue'

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
})
