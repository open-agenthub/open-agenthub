// @vitest-environment happy-dom
import { expect, it, vi } from 'vitest'
import { flushPromises, mount } from '@vue/test-utils'
import SessionWorkspace from './SessionWorkspace.vue'
vi.mock('../api.js', async original => {
  const actual = await original()
  return { ...actual, api: { ...actual.api,
    sessionFileCapabilities: vi.fn().mockResolvedValue({}),
    listSessionFiles: vi.fn().mockResolvedValue([]),
    getFilePresentation: vi.fn().mockResolvedValue(null) } }
})
const BrowserStub = { template: '<div data-browser-stub />', emits: ['popout-change'] }
function mountWorkspace() {
  return mount(SessionWorkspace, {
    props: { session: { id: 'layout', browser: { phase: 'Running' } } },
    slots: { default: '<div>Conversation</div>' },
    global: { stubs: { BrowserPane: BrowserStub } }
  })
}
it('collapses the companion and keeps the browser mounted until it returns', async () => {
  const wrapper = mountWorkspace()
  const pane = wrapper.findComponent(BrowserStub)
  pane.vm.$emit('popout-change', true)
  await flushPromises()
  expect(wrapper.get('.companion-side').attributes('style')).toContain('display: none')
  expect(wrapper.get('.split-seam').attributes('style')).toContain('display: none')
  expect(wrapper.get('.browser-return').text()).toContain('Bring back')
  expect(wrapper.findComponent(BrowserStub).vm).toBe(pane.vm)
  pane.vm.$emit('popout-change', false)
  await flushPromises()
  expect(wrapper.get('.companion-side').attributes('style') || '').not.toContain('display: none')
  expect(wrapper.find('.browser-return').exists()).toBe(false)
  wrapper.unmount()
})
it('shows Files when browser is popped out and collapses after Files closes', async () => {
  const wrapper = mountWorkspace()
  await wrapper.vm.openFiles()
  wrapper.findComponent(BrowserStub).vm.$emit('popout-change', true)
  await flushPromises()
  expect(wrapper.get('.companion-side').attributes('style') || '').not.toContain('display: none')
  expect(wrapper.get('[data-workspace-tab="files"]').element.parentElement.classList.contains('active')).toBe(true)
  await wrapper.get('[data-files-close]').trigger('click')
  expect(wrapper.get('.companion-side').attributes('style')).toContain('display: none')
  wrapper.unmount()
})

it('keeps Files selected when a stopped browser closes its pop-out during unmount', async () => {
  const ClosingBrowser = {
    template: '<div />', emits: ['popout-change'],
    beforeUnmount() { this.$emit('popout-change', false) }
  }
  const wrapper = mount(SessionWorkspace, {
    props: { session: { id: 'layout', browser: { phase: 'Running' } } },
    global: { stubs: { BrowserPane: ClosingBrowser } }
  })
  await wrapper.vm.openFiles()
  wrapper.findComponent(ClosingBrowser).vm.$emit('popout-change', true)
  await flushPromises()
  await wrapper.setProps({ session: { id: 'layout', browser: { phase: 'Stopped' } } })
  expect(wrapper.get('[data-workspace-tab="files"]').element.parentElement.classList.contains('active')).toBe(true)
  expect(wrapper.find('.browser-return').exists()).toBe(false)
  wrapper.unmount()
})
