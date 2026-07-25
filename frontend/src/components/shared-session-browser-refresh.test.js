// @vitest-environment happy-dom
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { flushPromises, mount } from '@vue/test-utils'
import SharedSessionView from './SharedSessionView.vue'
import { getSharedSession } from '../api.js'

vi.mock('../api.js', () => ({ getSharedSession: vi.fn() }))

describe('shared browser phase refresh', () => {
  beforeEach(() => { vi.useFakeTimers(); vi.clearAllMocks() })
  afterEach(() => vi.useRealTimers())

  it('refreshes shared session state so a later browser start becomes visible', async () => {
    getSharedSession
      .mockResolvedValueOnce({ id: 's1', accessRole: 'Viewer', browser: { phase: 'Stopped' } })
      .mockResolvedValueOnce({ id: 's1', accessRole: 'Viewer', browser: { phase: 'Running' } })
    const wrapper = mount(SharedSessionView, {
      props: { token: 'share-token' },
      global: { stubs: { TerminalView: { props: ['session'], template: '<div data-phase>{{ session.browser.phase }}</div>' } } }
    })
    await flushPromises()
    expect(wrapper.get('[data-phase]').text()).toBe('Stopped')

    await vi.advanceTimersByTimeAsync(2000)
    await flushPromises()
    expect(wrapper.get('[data-phase]').text()).toBe('Running')
    wrapper.unmount()
  })

  it('does not install polling after an unmount during the initial request', async () => {
    let resolveRequest
    getSharedSession.mockImplementationOnce(() => new Promise(resolve => { resolveRequest = resolve }))
    const wrapper = mount(SharedSessionView, {
      props: { token: 'share-token' },
      global: { stubs: { TerminalView: true } }
    })

    wrapper.unmount()
    resolveRequest({ id: 's1', accessRole: 'Viewer', browser: { phase: 'Running' } })
    await flushPromises()
    await vi.advanceTimersByTimeAsync(4000)

    expect(getSharedSession).toHaveBeenCalledTimes(1)
  })

  it('clears stale session UI when authorization refresh fails', async () => {
    getSharedSession
      .mockResolvedValueOnce({ id: 's1', accessRole: 'Viewer', browser: { phase: 'Running' } })
      .mockRejectedValueOnce(new Error('share revoked'))
    const wrapper = mount(SharedSessionView, {
      props: { token: 'share-token' },
      global: { stubs: { TerminalView: { template: '<div data-terminal>terminal</div>' } } }
    })
    await flushPromises()
    expect(wrapper.find('[data-terminal]').exists()).toBe(true)

    await vi.advanceTimersByTimeAsync(2000)
    await flushPromises()

    expect(wrapper.find('[data-terminal]').exists()).toBe(false)
    expect(wrapper.text()).toContain('share revoked')
    wrapper.unmount()
  })})
