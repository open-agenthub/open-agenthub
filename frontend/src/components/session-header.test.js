// @vitest-environment happy-dom
// The session bar keeps a fixed set of primary controls in one row; the rest lives in the "⋯"
// session menu (#551). These pin which control is where, per role.
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { flushPromises, mount } from '@vue/test-utils'
import TerminalView from './TerminalView.vue'
import SessionMenu from './workspace/SessionMenu.vue'

const mocks = vi.hoisted(() => ({
  api: {
    getTranscript: vi.fn().mockResolvedValue(''),
    getConversation: vi.fn().mockResolvedValue({ source: 'scrollback', entries: [], text: '', nextOffset: 0, length: 0 }),
    listPermissions: vi.fn().mockResolvedValue([]),
    listSessionMessages: vi.fn().mockResolvedValue([]),
    listProviderAccounts: vi.fn().mockResolvedValue({}),
    providerSwitchStatus: vi.fn().mockResolvedValue({ available: false, reason: 'Not now.' }),
    sessionModels: vi.fn().mockResolvedValue({ selected: 'model-a', switchMode: 'live', canSwitch: true, models: [] })
  }
}))

vi.mock('../api.js', () => ({
  api: mocks.api,
  config: {},
  getSharedTranscript: vi.fn().mockResolvedValue(''),
  getSharedConversation: vi.fn().mockResolvedValue({ source: 'scrollback', entries: [], text: '', nextOffset: 0, length: 0 })
}))

const longEmail = 'firstname.lastname-with-a-long-alias@engineering.your-org.example'
const session = (extra = {}) => ({ id: 's1', title: 'Coder', phase: 'Running', mode: 'Interactive', agent: 'Claude',
  authMode: 'Subscription', credentialId: 'work0001', accessRole: 'Owner', ...extra })

function mountTerminal(props = {}) {
  return mount(TerminalView, {
    props: { session: session(), ...props },
    attachTo: document.body,
    global: { stubs: { TerminalPane: true, ShareSessionDialog: true, ContinueLocallyDialog: true } }
  })
}
// The bar's own row, without what the menu holds.
const barOnly = wrapper => {
  const bar = wrapper.get('.term-bar').element.cloneNode(true)
  bar.querySelector('[data-session-menu-panel]')?.remove()
  return bar
}

describe('session bar (#551)', () => {
  let wrapper
  beforeEach(() => {
    vi.clearAllMocks()
    mocks.api.listProviderAccounts.mockResolvedValue({ Claude: [
      { id: 'work0001', label: 'Work', email: longEmail, isDefault: true },
      { id: 'home0002', label: 'Home', email: 'me@example.com' }
    ] })
  })
  afterEach(() => wrapper?.unmount())

  it('keeps only the primary controls in the bar for an owner', async () => {
    wrapper = mountTerminal()
    await flushPromises()
    const bar = barOnly(wrapper)
    expect(bar.querySelector('[data-model-switch]')).not.toBeNull()
    expect(bar.querySelector('[data-open-workspace]')).not.toBeNull()
    expect(bar.querySelector('[data-share-toggle]')).not.toBeNull()
    expect([...bar.querySelectorAll('button')].some(b => b.getAttribute('aria-label') === 'Pause')).toBe(true)
    for (const hook of ['data-account-select', 'data-open-files', 'data-send-message-toggle', 'data-edit-session',
      'data-continue-locally-toggle', 'data-provider-switch-open'])
      expect(bar.querySelector(`[${hook}]`), hook).toBeNull()
    // The long e-mail is nowhere in the bar's row; the menu shows it in full.
    expect(bar.textContent).not.toContain(longEmail)
    const panel = wrapper.get('[data-session-menu-panel]')
    expect(panel.get('[data-account-identity]').text()).toBe(longEmail)
    for (const hook of ['data-account-select', 'data-open-files', 'data-send-message-toggle', 'data-edit-session',
      'data-continue-locally-toggle', 'data-provider-switch-open'])
      expect(panel.find(`[${hook}]`).exists(), hook).toBe(true)
  })

  it('names accounts in the menu as the person named them, with the full e-mail underneath', async () => {
    mocks.api.listProviderAccounts.mockResolvedValue({ Claude: [
      { id: 'work0001', label: 'Client project', email: longEmail, isDefault: true },
      { id: 'home0002', label: '', email: 'me@example.com' }
    ] })
    wrapper = mountTerminal()
    await flushPromises()
    expect(wrapper.findAll('[data-account-option]').map(o => o.text())).toEqual(['Client project', 'me'])
    expect(wrapper.get('[data-account-option="work0001"]').attributes('title')).toBe(`Client project — ${longEmail}`)
    expect(wrapper.get('[data-account-identity]').text()).toBe(longEmail)
  })

  it('polls the provider switch only while its panel is open, though it stays mounted in the menu', async () => {
    vi.useFakeTimers()
    try {
      wrapper = mountTerminal()
      await flushPromises()
      // One read on mount tells whether a replacement is already running.
      expect(mocks.api.providerSwitchStatus).toHaveBeenCalledTimes(1)
      await vi.advanceTimersByTimeAsync(20000)
      expect(mocks.api.providerSwitchStatus).toHaveBeenCalledTimes(1)
      await wrapper.get('[data-session-menu-toggle]').trigger('click')
      await wrapper.get('[data-provider-switch-open]').trigger('click')
      await flushPromises()
      const opened = mocks.api.providerSwitchStatus.mock.calls.length
      await vi.advanceTimersByTimeAsync(4100)
      expect(mocks.api.providerSwitchStatus.mock.calls.length).toBeGreaterThanOrEqual(opened + 2)
      // Closed again: no more reads.
      await wrapper.get('[data-session-menu-toggle]').trigger('click')
      await wrapper.get('[data-provider-switch-open]').trigger('click')
      await flushPromises()
      const closed = mocks.api.providerSwitchStatus.mock.calls.length
      await vi.advanceTimersByTimeAsync(20000)
      expect(mocks.api.providerSwitchStatus).toHaveBeenCalledTimes(closed)
    } finally { vi.useRealTimers() }
  })

  it('marks the closed menu while a provider replacement is pending', async () => {
    mocks.api.providerSwitchStatus.mockResolvedValue({ available: false,
      operation: { request: { target: { agent: 'Codex' } }, progress: { phase: 'requested' } } })
    wrapper = mountTerminal()
    await flushPromises()
    expect(wrapper.get('[data-session-menu-toggle]').attributes('aria-label')).toBe('Session menu — provider replacement running')
    expect(wrapper.get('[data-provider-switch-open]').text()).toBe('Provider replacement…')
    mocks.api.providerSwitchStatus.mockResolvedValue({ available: false, reason: 'Not now.' })
  })

  it('marks the closed menu when the session record says a replacement is held', async () => {
    wrapper = mountTerminal({ session: session({ providerSwitchPending: true }) })
    await flushPromises()
    expect(wrapper.find('[data-session-menu-attention]').exists()).toBe(true)
  })

  it('gives a viewer the view switch, the read-only model and Files only', async () => {
    wrapper = mountTerminal({ session: session({ accessRole: 'Viewer' }) })
    await flushPromises()
    const bar = barOnly(wrapper)
    expect(bar.querySelector('[data-share-toggle]')).toBeNull()
    expect(bar.querySelector('[data-model-readonly], [data-model-switch]')).not.toBeNull()
    expect(bar.textContent).toContain('Read-only')
    const items = wrapper.get('[data-session-menu-panel]').findAll('button').map(b => b.text())
    expect(items).toEqual(['Files'])
  })

  it('gives a shared-link reader no account, model switch or management', async () => {
    wrapper = mountTerminal({ sharedToken: 'tok', session: session({ accessRole: 'Viewer' }) })
    await flushPromises()
    expect(wrapper.find('[data-model-switch]').exists()).toBe(false)
    expect(wrapper.find('[data-share-toggle]').exists()).toBe(false)
    expect(wrapper.get('[data-session-menu-panel]').findAll('button').map(b => b.text())).toEqual(['Files'])
  })

  it('shows the connection as a dot whose words are its accessible name', async () => {
    wrapper = mountTerminal()
    await flushPromises()
    const dot = wrapper.get('[data-connection-status]')
    expect(dot.attributes('aria-label')).toBe('Agent: connecting…')
    expect(dot.classes()).toContain('pending')
    wrapper.findComponent({ name: 'TerminalPane' }).vm.$emit('status', 'connected')
    await flushPromises()
    expect(dot.attributes('aria-label')).toBe('Agent: connected')
    expect(dot.attributes('title')).toBe('Agent: connected')
    expect(dot.classes()).toContain('ok')
    expect(dot.text()).toBe('')
  })

  it('marks the closed menu when messages are open', async () => {
    mocks.api.listSessionMessages.mockResolvedValue([{ id: 'm1', body: 'hi', createdAt: '2026-11-08T10:00:00Z', deliveredAt: null }])
    wrapper = mountTerminal()
    await flushPromises()
    const toggle = wrapper.get('[data-session-menu-toggle]')
    expect(toggle.attributes('aria-label')).toBe('Session menu — 1 open message')
    expect(wrapper.find('[data-session-menu-attention]').exists()).toBe(true)
  })

  it('opens the provider panel from the menu and closes the menu', async () => {
    wrapper = mountTerminal()
    await flushPromises()
    await wrapper.get('[data-session-menu-toggle]').trigger('click')
    expect(wrapper.get('[data-session-menu-panel]').isVisible()).toBe(true)
    await wrapper.get('[data-provider-switch-open]').trigger('click')
    await flushPromises()
    expect(wrapper.find('[data-provider-panel]').exists()).toBe(true)
    expect(wrapper.get('[data-session-menu-toggle]').attributes('aria-expanded')).toBe('false')
  })
})

describe('SessionMenu keyboard', () => {
  let wrapper
  afterEach(() => wrapper?.unmount())
  const mountMenu = () => mount(SessionMenu, {
    attachTo: document.body,
    slots: { default: '<button class="a">A</button><select class="s"><option>x</option></select><button class="b" data-menu-closes>B</button>' }
  })

  it('opens on the button, focuses the first item and moves with the arrow keys', async () => {
    wrapper = mountMenu()
    const toggle = wrapper.get('[data-session-menu-toggle]')
    expect(toggle.attributes('aria-expanded')).toBe('false')
    expect(toggle.attributes('aria-controls')).toBe(wrapper.get('[data-session-menu-panel]').attributes('id'))
    await toggle.trigger('click')
    await flushPromises()
    expect(toggle.attributes('aria-expanded')).toBe('true')
    expect(document.activeElement).toBe(wrapper.get('.a').element)
    await wrapper.get('.a').trigger('keydown', { key: 'ArrowDown' })
    expect(document.activeElement).toBe(wrapper.get('.s').element)
    // Inside the select the arrow keys change its value; the menu leaves them alone.
    await wrapper.get('.s').trigger('keydown', { key: 'ArrowDown' })
    expect(document.activeElement).toBe(wrapper.get('.s').element)
    await wrapper.get('.a').element.focus()
    await wrapper.get('.a').trigger('keydown', { key: 'ArrowUp' })
    expect(document.activeElement).toBe(wrapper.get('.b').element)
  })

  it('closes on Escape and returns focus to its button', async () => {
    wrapper = mountMenu()
    const toggle = wrapper.get('[data-session-menu-toggle]')
    await toggle.trigger('click')
    await flushPromises()
    await wrapper.get('.a').trigger('keydown', { key: 'Escape' })
    expect(toggle.attributes('aria-expanded')).toBe('false')
    expect(document.activeElement).toBe(toggle.element)
  })

  it('closes after an item marked to close it and on a click outside', async () => {
    wrapper = mountMenu()
    const toggle = wrapper.get('[data-session-menu-toggle]')
    await toggle.trigger('click')
    await wrapper.get('.b').trigger('click')
    expect(toggle.attributes('aria-expanded')).toBe('false')
    await toggle.trigger('click')
    await wrapper.get('.a').trigger('click')
    expect(toggle.attributes('aria-expanded')).toBe('true')
    document.body.dispatchEvent(new Event('pointerdown', { bubbles: true }))
    await flushPromises()
    expect(toggle.attributes('aria-expanded')).toBe('false')
  })
})
