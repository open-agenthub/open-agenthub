// @vitest-environment happy-dom
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { flushPromises, mount } from '@vue/test-utils'
import SharedSessionView from './SharedSessionView.vue'
import { getSharedSession } from '../api.js'

vi.mock('../api.js', () => ({ getSharedSession: vi.fn() }))

describe('shared browser phase refresh', () => {
  beforeEach(() => vi.useFakeTimers())
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
})
