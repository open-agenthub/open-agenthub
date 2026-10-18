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
afterEach(() => { for (const wrapper of viewers.splice(0)) wrapper.unmount() })
it('opens Changes only for the viewer who opts in and unmounts on close', async () => {
  const first = viewer(), second = viewer(); await flushPromises()
  expect(first.findComponent({ name: 'WorkspaceDiffPane' }).exists()).toBe(false)
  await first.get('.diff-toggle button').trigger('click')
  expect(first.findComponent({ name: 'WorkspaceDiffPane' }).exists()).toBe(true)
  expect(second.findComponent({ name: 'WorkspaceDiffPane' }).exists()).toBe(false)
  await first.get('[aria-label="Close changes"]').trigger('click')
  expect(first.findComponent({ name: 'WorkspaceDiffPane' }).exists()).toBe(false)
})
it('keeps Changes visible after closing the other companion and resets on session navigation', async () => {
  const wrapper = viewer('Running'); await flushPromises()
  await wrapper.get('.diff-toggle button').trigger('click')
  await wrapper.get('[data-workspace-tab="browser"]').trigger('click')
  expect(wrapper.findComponent({ name: 'WorkspaceDiffPane' }).props('active')).toBe(false)
  await wrapper.get('[data-browser-close]').trigger('click')
  expect(wrapper.findComponent({ name: 'WorkspaceDiffPane' }).props('active')).toBe(true)
  await wrapper.setProps({ session: { id: 's2', browser: { phase: 'Stopped' } } })
  expect(wrapper.findComponent({ name: 'WorkspaceDiffPane' }).exists()).toBe(false)
})
