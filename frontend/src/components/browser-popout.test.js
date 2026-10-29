// @vitest-environment happy-dom
import { afterEach, expect, it, vi } from 'vitest'
import { mount } from '@vue/test-utils'
import BrowserPopout from './BrowserPopout.vue'
afterEach(() => vi.unstubAllGlobals())
it('accepts state only from its opener at the same origin', async () => {
  const opener = { postMessage: vi.fn() }
  vi.stubGlobal('opener', opener)
  const wrapper = mount(BrowserPopout, { global: { stubs: { BrowserPane: true } } })
  expect(opener.postMessage).toHaveBeenCalledWith({ type: 'browser-popout-ready' }, location.origin)
  const data = { type: 'browser-popout-state', session: { id: 's1' }, canWrite: true, sharedToken: 'private-share' }
  async function message(source, origin) {
    window.dispatchEvent(new MessageEvent('message', { source, origin, data }))
    await wrapper.vm.$nextTick()
  }
  await message(opener, 'https://example.com')
  await message(window, location.origin)
  expect(wrapper.find('browser-pane-stub').exists()).toBe(false)
  await message(opener, location.origin)
  expect(wrapper.findComponent({ name: 'BrowserPane' }).props('sharedToken')).toBe('private-share')
  await wrapper.get('button').trigger('click')
  expect(opener.postMessage).toHaveBeenLastCalledWith({ type: 'browser-popout-return' }, location.origin)
  wrapper.unmount()
})
