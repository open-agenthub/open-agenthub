// @vitest-environment happy-dom
import { beforeEach, describe, expect, it, vi } from 'vitest'
import { flushPromises, mount } from '@vue/test-utils'
import SettingsDialog from './SettingsDialog.vue'

const mocks = vi.hoisted(() => ({
  config: { gitEnabled: false, slackEnabled: false, telegramEnabled: false, signalEnabled: false },
  api: {
    listApiTokens: vi.fn(),
    slackMe: vi.fn(), setSlackPrefs: vi.fn(),
    chatMe: vi.fn(), telegramLinkCode: vi.fn(), setTelegramPrefs: vi.fn(),
    unlinkTelegram: vi.fn(), setSignalPrefs: vi.fn(), verifySignal: vi.fn()
  }
}))

vi.mock('../api.js', () => ({ api: mocks.api, config: mocks.config }))

const chatMeDefault = () => ({
  telegram: { configured: true, linked: false, forum: false, enabled: true },
  signal: { configured: true, number: null, verified: false, enabled: true }
})

function mountDialog() {
  return mount(SettingsDialog, { props: { embedded: true, section: 'notifications' } })
}

describe('chat settings sections', () => {
  beforeEach(() => {
    vi.clearAllMocks()
    Object.assign(mocks.config, { gitEnabled: false, slackEnabled: false, telegramEnabled: false, signalEnabled: false })
    mocks.api.listApiTokens.mockResolvedValue([])
    mocks.api.chatMe.mockResolvedValue(chatMeDefault())
  })

  it('renders the telegram section and generates a link code', async () => {
    mocks.config.telegramEnabled = true
    mocks.api.telegramLinkCode.mockResolvedValue({
      code: 'ABC123', botUsername: 'examplebot', deepLink: 'https://t.me/examplebot?start=ABC123'
    })
    const wrapper = mountDialog()
    await flushPromises()

    expect(mocks.api.chatMe).toHaveBeenCalled()
    expect(wrapper.find('[data-section="telegram"]').exists()).toBe(true)
    expect(wrapper.get('[data-telegram-status]').text()).toContain('not linked')

    await wrapper.get('[data-telegram-generate]').trigger('click')
    await flushPromises()

    expect(mocks.api.telegramLinkCode).toHaveBeenCalled()
    expect(wrapper.get('[data-telegram-code]').text()).toBe('ABC123')
    expect(wrapper.get('[data-telegram-link] a').attributes('href')).toBe('https://t.me/examplebot?start=ABC123')
  })

  it('shows linked status with forum hint and unlinks', async () => {
    mocks.config.telegramEnabled = true
    mocks.api.chatMe.mockResolvedValue({
      ...chatMeDefault(),
      telegram: { configured: true, linked: true, forum: true, enabled: true }
    })
    mocks.api.unlinkTelegram.mockResolvedValue(null)
    const wrapper = mountDialog()
    await flushPromises()

    expect(wrapper.get('[data-telegram-status]').text()).toContain('linked ✓ (forum group)')

    await wrapper.get('[data-telegram-unlink]').trigger('click')
    await flushPromises()
    expect(mocks.api.unlinkTelegram).toHaveBeenCalled()
  })

  it('hides the telegram section when not configured', async () => {
    mocks.config.signalEnabled = true
    const wrapper = mountDialog()
    await flushPromises()

    expect(wrapper.find('[data-section="telegram"]').exists()).toBe(false)
    expect(wrapper.find('[data-section="signal"]').exists()).toBe(true)
  })

  it('saves a signal number, shows the verification hint on 202, then verifies', async () => {
    mocks.config.signalEnabled = true
    mocks.api.setSignalPrefs.mockResolvedValue(202)
    mocks.api.verifySignal.mockResolvedValue(204)
    const wrapper = mountDialog()
    await flushPromises()

    await wrapper.get('[data-signal-number]').setValue(' +1 555 123-4567 ')
    await wrapper.get('[data-signal-save]').trigger('click')
    await flushPromises()

    expect(mocks.api.setSignalPrefs).toHaveBeenCalledWith({ enabled: true, number: '+1 555 123-4567' })
    expect(wrapper.get('[data-signal-msg]').text()).toContain('Verification code sent via Signal')

    await wrapper.get('[data-signal-code]').setValue('654321')
    await wrapper.get('[data-signal-verify]').trigger('click')
    await flushPromises()

    expect(mocks.api.verifySignal).toHaveBeenCalledWith('654321')
    expect(wrapper.get('[data-signal-verify-msg]').text()).toContain('verified ✓')
  })

  it('maps signal error statuses to inline messages', async () => {
    mocks.config.signalEnabled = true
    const err = new Error('409 taken'); err.status = 409
    mocks.api.setSignalPrefs.mockRejectedValue(err)
    const wrapper = mountDialog()
    await flushPromises()

    await wrapper.get('[data-signal-number]').setValue('+15551234567')
    await wrapper.get('[data-signal-save]').trigger('click')
    await flushPromises()

    expect(wrapper.get('[data-signal-msg]').text()).toContain('already linked to another account')
  })

  it('hides the signal section when not configured', async () => {
    mocks.config.telegramEnabled = true
    const wrapper = mountDialog()
    await flushPromises()

    expect(wrapper.find('[data-section="signal"]').exists()).toBe(false)
  })

  it('skips loading chat settings when neither platform is configured', async () => {
    const wrapper = mountDialog()
    await flushPromises()

    expect(mocks.api.chatMe).not.toHaveBeenCalled()
    expect(wrapper.text()).toContain('No chat integration')
  })
})
