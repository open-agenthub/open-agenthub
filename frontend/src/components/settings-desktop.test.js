// @vitest-environment happy-dom
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { flushPromises, mount } from '@vue/test-utils'
import SettingsDialog from './SettingsDialog.vue'

const mocks = vi.hoisted(() => ({
  config: { gitEnabled: false, slackEnabled: false, telegramEnabled: false, signalEnabled: false },
  api: { listApiTokens: vi.fn(), slackMe: vi.fn(), chatMe: vi.fn() }
}))

vi.mock('../api.js', () => ({ api: mocks.api, config: mocks.config }))

function stubNotification(permission) {
  const requestPermission = vi.fn().mockResolvedValue(permission)
  globalThis.Notification = { permission, requestPermission }
  return requestPermission
}

function mountDialog() {
  return mount(SettingsDialog, { props: { embedded: true, section: 'notifications' } })
}

describe('desktop notifications toggle', () => {
  beforeEach(() => {
    vi.clearAllMocks()
    localStorage.clear()
    mocks.api.listApiTokens.mockResolvedValue([])
  })
  afterEach(() => { delete globalThis.Notification })

  it('is shown even when no chat platform is configured, unticked by default', async () => {
    const wrapper = mountDialog()
    await flushPromises()
    expect(wrapper.find('[data-section="desktop"]').exists()).toBe(true)
    expect(wrapper.get('[data-desktop-toggle]').element.checked).toBe(false)
  })

  it('reflects a previously saved opt-in', async () => {
    localStorage.setItem('desktopNotify', '1')
    const wrapper = mountDialog()
    await flushPromises()
    expect(wrapper.get('[data-desktop-toggle]').element.checked).toBe(true)
  })

  it('enables and persists when the browser grants permission', async () => {
    stubNotification('granted')
    const wrapper = mountDialog()
    await flushPromises()

    await wrapper.get('[data-desktop-toggle]').setValue(true)
    await flushPromises()

    expect(localStorage.getItem('desktopNotify')).toBe('1')
    expect(wrapper.get('[data-desktop-toggle]').element.checked).toBe(true)
    expect(wrapper.find('[data-desktop-msg]').exists()).toBe(false)
  })

  it('unticks and shows the inline note when permission is denied', async () => {
    stubNotification('denied')
    const wrapper = mountDialog()
    await flushPromises()

    await wrapper.get('[data-desktop-toggle]').setValue(true)
    await flushPromises()

    expect(localStorage.getItem('desktopNotify')).toBe('0')
    expect(wrapper.get('[data-desktop-toggle]').element.checked).toBe(false)
    expect(wrapper.get('[data-desktop-msg]').text()).toContain('blocked by the browser')
  })

  it('shows the unsupported note when the browser has no Notification API', async () => {
    delete globalThis.Notification
    const wrapper = mountDialog()
    await flushPromises()

    await wrapper.get('[data-desktop-toggle]').setValue(true)
    await flushPromises()

    expect(localStorage.getItem('desktopNotify')).toBe('0')
    expect(wrapper.get('[data-desktop-toggle]').element.checked).toBe(false)
    expect(wrapper.get('[data-desktop-msg]').text()).toBe('Desktop notifications are not supported by this browser.')
  })

  it('asks for permission and honours the resolved value, not the stale static', async () => {
    // Static permission stays 'default'; only the resolved promise says 'granted'.
    const requestPermission = stubNotification('default')
    requestPermission.mockResolvedValue('granted')
    const wrapper = mountDialog()
    await flushPromises()

    await wrapper.get('[data-desktop-toggle]').setValue(true)
    await flushPromises()

    expect(requestPermission).toHaveBeenCalled()
    expect(localStorage.getItem('desktopNotify')).toBe('1')
    expect(wrapper.get('[data-desktop-toggle]').element.checked).toBe(true)
    expect(wrapper.find('[data-desktop-msg]').exists()).toBe(false)
  })

  it('disables without touching the Notification API', async () => {
    localStorage.setItem('desktopNotify', '1')
    const wrapper = mountDialog()
    await flushPromises()

    await wrapper.get('[data-desktop-toggle]').setValue(false)
    await flushPromises()

    expect(localStorage.getItem('desktopNotify')).toBe('0')
    expect(wrapper.get('[data-desktop-toggle]').element.checked).toBe(false)
    expect(wrapper.find('[data-desktop-msg]').exists()).toBe(false)
  })
})
