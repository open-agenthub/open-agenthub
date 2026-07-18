// @vitest-environment happy-dom
import { beforeEach, afterEach, describe, expect, it, vi } from 'vitest'
import { flushPromises, mount } from '@vue/test-utils'
import App from './App.vue'
import AdminView from './components/AdminView.vue'

const mocks = vi.hoisted(() => ({
  api: {
    listSessions: vi.fn(), listProjects: vi.fn(), adminAccess: vi.fn(),
    resumeSession: vi.fn(), pauseSession: vi.fn(), deleteSession: vi.fn(),
    activateLicense: vi.fn(), adminOverview: vi.fn(), startLicenseCheckout: vi.fn(),
    setUserSeat: vi.fn(), deactivateLicense: vi.fn()
  },
  auth: { enabled: false, isAuthenticated: true, user: 'tester', email: 'tester@example.dev', login: vi.fn(), logout: vi.fn() }
}))

vi.mock('./api.js', () => ({ api: mocks.api, auth: mocks.auth }))
vi.mock('../api.js', () => ({ api: mocks.api, auth: mocks.auth }))

const appStubs = {
  ProjectSidebar: true, TerminalView: true, AdminView: true, NewSessionDialog: true,
  EditSessionDialog: true, DuplicateSessionDialog: true, ShareSessionDialog: true,
  SharedSessionView: true, HomeView: true, SessionsView: true, UsageView: true,
  SettingsView: { props: ['initialTab'], template: '<div class="settings-tab">{{ initialTab }}</div>' }
}

describe('license checkout return', () => {
  beforeEach(() => {
    vi.clearAllMocks()
    vi.spyOn(globalThis, 'setInterval').mockReturnValue(1)
    mocks.api.listSessions.mockResolvedValue([])
    mocks.api.listProjects.mockResolvedValue([])
    mocks.api.adminAccess.mockResolvedValue({ isAdmin: true })
    mocks.api.activateLicense.mockResolvedValue({ valid: true })
    history.replaceState({}, '', '/')
  })
  afterEach(() => vi.restoreAllMocks())

  it('activates the returned license and opens the users tab', async () => {
    history.replaceState({}, '', '/license/activate?license=tok-123')
    const wrapper = mount(App, { global: { stubs: appStubs } })
    await flushPromises()

    expect(mocks.api.activateLicense).toHaveBeenCalledWith('tok-123')
    expect(wrapper.get('.settings-tab').text()).toBe('users')
    expect(wrapper.get('.banner.ok').text()).toContain('activated')
    expect(location.pathname).toBe('/')
  })

  it('shows a pending banner when payment is still settling', async () => {
    history.replaceState({}, '', '/license/activate?checkout=pending')
    const wrapper = mount(App, { global: { stubs: appStubs } })
    await flushPromises()

    expect(mocks.api.activateLicense).not.toHaveBeenCalled()
    expect(wrapper.get('.banner.warn').text()).toContain('email')
  })

  it('surfaces activation failures', async () => {
    mocks.api.activateLicense.mockRejectedValue(new Error('bad token'))
    history.replaceState({}, '', '/license/activate?license=broken')
    const wrapper = mount(App, { global: { stubs: appStubs } })
    await flushPromises()

    expect(wrapper.get('.banner.error').text()).toContain('bad token')
  })
})

describe('AdminView license states', () => {
  beforeEach(() => {
    vi.clearAllMocks()
    mocks.api.startLicenseCheckout.mockResolvedValue({ url: 'https://stripe.example/session' })
  })

  const overview = (valid) => ({
    isAdmin: true,
    license: valid
      ? { valid: true, present: true, plan: 'subscription', seats: 5, org: 'ACME', validUntil: '2027-01-01' }
      : { valid: false, present: false, reason: 'No license activated.' },
    seats: { used: 2, included: valid ? 5 : 0 },
    users: [
      { owner: 'alice', licensed: true, updatedAt: '2026-07-01' },
      { owner: 'bob', licensed: true, updatedAt: '2026-07-02' }
    ],
    billingPortalUrl: '',
    lastCheckIn: null
  })

  it('shows unlicensed badges and the get-license button without a license', async () => {
    mocks.api.adminOverview.mockResolvedValue(overview(false))
    const wrapper = mount(AdminView, { props: { embedded: true, section: 'seats' } })
    await flushPromises()

    expect(wrapper.findAll('[data-user-unlicensed]')).toHaveLength(2)
    expect(wrapper.find('input[type="checkbox"]').exists()).toBe(false)
    expect(wrapper.find('[data-get-license]').exists()).toBe(true)
  })

  it('starts the checkout with a same-origin return URL', async () => {
    mocks.api.adminOverview.mockResolvedValue(overview(false))
    const origin = location.origin
    const wrapper = mount(AdminView, { props: { embedded: true, section: 'billing' } })
    await flushPromises()

    await wrapper.get('[data-get-license]').trigger('click')
    const emailInput = wrapper.get('input[type="email"]')
    await emailInput.setValue('billing@acme.dev')
    await wrapper.get('.checkout-form input[placeholder="ACME GmbH"]').setValue('ACME')
    await wrapper.get('[data-start-checkout]').trigger('click')
    await flushPromises()

    expect(mocks.api.startLicenseCheckout).toHaveBeenCalledWith(expect.objectContaining({
      email: 'billing@acme.dev',
      org: 'ACME',
      returnUrl: `${origin}/license/activate`
    }))
  })

  it('keeps seat checkboxes when the license is active', async () => {
    mocks.api.adminOverview.mockResolvedValue(overview(true))
    const wrapper = mount(AdminView, { props: { embedded: true, section: 'seats' } })
    await flushPromises()

    expect(wrapper.findAll('input[type="checkbox"]')).toHaveLength(2)
    expect(wrapper.find('[data-user-unlicensed]').exists()).toBe(false)
    expect(wrapper.find('[data-get-license]').exists()).toBe(false)
  })
})
