// @vitest-environment happy-dom
import { afterEach, beforeEach, expect, it, vi } from 'vitest'
import { flushPromises, mount } from '@vue/test-utils'
import WorkspaceDiffPane from './WorkspaceDiffPane.vue'
const mocks = vi.hoisted(() => ({ read: vi.fn(), shared: vi.fn() }))
vi.mock('../../api.js', () => ({ api: { workspaceDiff: mocks.read }, getSharedWorkspaceDiff: mocks.shared }))
let wrapper
const response = { repositories: [{ path: 'repo', base: '123456789012345', files: [{ path: '<script>.txt', patch: '+<img src=x onerror=alert(1)>\n-old\n', omitted: null }, { path: 'image.png', omitted: 'binary' }] }], truncated: true }
beforeEach(() => { vi.useFakeTimers(); mocks.read.mockReset().mockResolvedValue(response); mocks.shared.mockReset().mockResolvedValue(response) })
afterEach(() => { wrapper?.unmount(); vi.useRealTimers() })
it('escapes names and patches, labels omissions and selects comparison', async () => {
  wrapper = mount(WorkspaceDiffPane, { props: { sessionId: 's1' } }); await flushPromises()
  expect(wrapper.text()).toContain('<script>.txt'); expect(wrapper.find('img').exists()).toBe(false)
  expect(wrapper.text()).toContain('Binary file'); expect(wrapper.text()).toContain('review limit')
  await wrapper.get('select').setValue('head'); await flushPromises()
  expect(mocks.read).toHaveBeenLastCalledWith('s1', 'head')
})
it('polls only while active and clears stale data on failures', async () => {
  wrapper = mount(WorkspaceDiffPane, { props: { sessionId: 's1', active: false } }); await flushPromises()
  expect(mocks.read).not.toHaveBeenCalled()
  await wrapper.setProps({ active: true }); await flushPromises(); expect(mocks.read).toHaveBeenCalledTimes(1)
  await vi.advanceTimersByTimeAsync(3000); expect(mocks.read).toHaveBeenCalledTimes(2)
  await wrapper.setProps({ active: false }); await vi.advanceTimersByTimeAsync(6000); expect(mocks.read).toHaveBeenCalledTimes(2)
  mocks.read.mockRejectedValue(new Error('offline'))
  await wrapper.setProps({ sessionId: 's2', active: true }); await flushPromises()
  expect(wrapper.text()).toContain('unavailable'); expect(wrapper.text()).not.toContain('<script>.txt')
})
it('uses shared read route and ignores an outstanding result after switching sessions', async () => {
  let resolve
  mocks.shared.mockImplementationOnce(() => new Promise(r => { resolve = r }))
  wrapper = mount(WorkspaceDiffPane, { props: { sessionId: 's1', sharedToken: 'share' } }); await flushPromises()
  expect(mocks.read).not.toHaveBeenCalled()
  await wrapper.setProps({ sessionId: 's2', sharedToken: 'new-share' }); await flushPromises()
  resolve({ repositories: [{ path: 'stale', files: [] }] }); await flushPromises()
  expect(wrapper.text()).not.toContain('stale'); expect(mocks.shared).toHaveBeenLastCalledWith('new-share', 'base')
})
