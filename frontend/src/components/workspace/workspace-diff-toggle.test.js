// @vitest-environment happy-dom
import { afterEach, expect, it, vi } from 'vitest'
import { flushPromises, mount } from '@vue/test-utils'
import SessionWorkspace from '../SessionWorkspace.vue'
vi.mock('../../api.js', () => ({
  api: { sessionFileCapabilities: async () => ({}), listSessionFiles: async () => [], getFilePresentation: async () => ({ revision: 0 }) },
  getSharedWorkspaceDiff: vi.fn(), getSharedFileCapabilities: vi.fn(), getSharedFilePresentation: vi.fn(),
  listSharedSessionFiles: vi.fn(), sessionEventsUrl: vi.fn(), sharedSessionEventsUrl: vi.fn()
}))
vi.mock('../../lib/live.js', () => ({ openSessionEvents: () => ({ close() {} }) }))
const viewers = []
function viewer(browser = 'Stopped') {
  const wrapper = mount(SessionWorkspace, { props: { session: { id: 's1', browser: { phase: browser } } },
    global: { stubs: { WorkspaceDiffPane: true, FilesPane: true, BrowserPane: true } }, slots: { default: '<div>Agent</div>' } })
  viewers.push(wrapper); return wrapper
}
const diff = wrapper => wrapper.findComponent({ name: 'WorkspaceDiffPane' })
async function openFiles(wrapper) { await wrapper.vm.openFiles(); await flushPromises() }
afterEach(() => { for (const wrapper of viewers.splice(0)) wrapper.unmount() })

it('reaches Changes only from the Files pane, with no button of its own', async () => {
  const wrapper = viewer(); await flushPromises()
  expect(wrapper.find('.diff-toggle').exists()).toBe(false)
  expect(wrapper.find('[data-files-view]').exists()).toBe(false)
  expect(wrapper.find('[data-workspace-tab="diff"]').exists()).toBe(false)
  await openFiles(wrapper)
  expect(wrapper.get('[data-files-view="files"]').attributes('aria-selected')).toBe('true')
  expect(diff(wrapper).exists()).toBe(false)
  await wrapper.get('[data-files-view="changes"]').trigger('click')
  expect(wrapper.get('[data-files-view="changes"]').attributes('aria-selected')).toBe('true')
  expect(diff(wrapper).exists()).toBe(true)
  expect(diff(wrapper).props('active')).toBe(true)
  expect(wrapper.findComponent({ name: 'FilesPane' }).attributes('style')).toContain('display: none')
})

it('opens Changes only for the viewer who opts in and unmounts it when Files closes', async () => {
  const first = viewer(), second = viewer(); await flushPromises()
  await openFiles(first); await openFiles(second)
  await first.get('[data-files-view="changes"]').trigger('click')
  expect(diff(first).exists()).toBe(true)
  expect(diff(second).exists()).toBe(false)
  await first.get('[data-files-view="files"]').trigger('click')
  expect(diff(first).exists()).toBe(false)
  await first.get('[data-files-view="changes"]').trigger('click')
  await first.get('[data-files-close]').trigger('click')
  expect(diff(first).exists()).toBe(false)
  await openFiles(first)
  expect(first.get('[data-files-view="files"]').attributes('aria-selected')).toBe('true')
  expect(diff(first).exists()).toBe(false)
})

it('pauses Changes behind the browser tab and resets on session navigation', async () => {
  const wrapper = viewer('Running'); await flushPromises()
  await openFiles(wrapper)
  await wrapper.get('[data-files-view="changes"]').trigger('click')
  await wrapper.get('[data-workspace-tab="browser"]').trigger('click')
  expect(diff(wrapper).props('active')).toBe(false)
  await wrapper.get('[data-browser-close]').trigger('click')
  expect(diff(wrapper).props('active')).toBe(true)
  await wrapper.get('[data-mobile-workspace-tab="agent"]').trigger('click')
  expect(diff(wrapper).props('active')).toBe(false)
  await wrapper.setProps({ session: { id: 's2', browser: { phase: 'Stopped' } } })
  expect(diff(wrapper).exists()).toBe(false)
})
