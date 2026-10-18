// @vitest-environment happy-dom
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { flushPromises, mount } from '@vue/test-utils'
import SecretRequestBanner from './SecretRequestBanner.vue'
import SecretsPane from './SecretsPane.vue'

// The owner's side of secret_request (docs/session-secrets.md): the session shows what its agent
// asked for, the link opens the pre-filled form, and saving there gives the secret to that session.
const mocks = vi.hoisted(() => ({
  api: {
    listSecrets: vi.fn(), upsertSecret: vi.fn(), listSecretRequests: vi.fn(), getSecretRequest: vi.fn(),
    provideSecretRequest: vi.fn(), refuseSecretRequest: vi.fn(), pauseSession: vi.fn(), resumeSession: vi.fn()
  }
}))
vi.mock('../api.js', () => ({ api: mocks.api }))

const request = { id: 'r1', sessionId: 's1', sessionTitle: 'Deploy bot', name: 'SENTRY_DSN', delivery: 'env', purpose: 'error reporting', status: 'pending' }

beforeEach(() => {
  vi.clearAllMocks()
  mocks.api.listSecrets.mockResolvedValue([])
  mocks.api.listSecretRequests.mockResolvedValue([request])
  mocks.api.getSecretRequest.mockResolvedValue(request)
  mocks.api.upsertSecret.mockResolvedValue({ id: 'sec1', name: 'SENTRY_DSN', delivery: 'env' })
  mocks.api.provideSecretRequest.mockResolvedValue({ secretId: 'sec1', name: 'SENTRY_DSN', delivery: 'env', restartRequired: true })
  mocks.api.refuseSecretRequest.mockResolvedValue(null)
  mocks.api.pauseSession.mockResolvedValue({})
  mocks.api.resumeSession.mockResolvedValue({})
})
afterEach(() => window.history.replaceState(null, '', '/'))

describe('the session shows what its agent asked for', () => {
  it('links to the pre-filled form and can refuse', async () => {
    const wrapper = mount(SecretRequestBanner, { props: { sessionId: 's1' } })
    await flushPromises()
    expect(mocks.api.listSecretRequests).toHaveBeenCalledWith('s1')
    expect(wrapper.text()).toContain('SENTRY_DSN')
    expect(wrapper.text()).toContain('error reporting')
    expect(wrapper.get('[data-secret-request-add]').attributes('href')).toBe('/settings/secrets?request=r1')
    mocks.api.listSecretRequests.mockResolvedValue([])
    await wrapper.get('[data-secret-request-refuse]').trigger('click')
    await flushPromises()
    expect(mocks.api.refuseSecretRequest).toHaveBeenCalledWith('r1')
    expect(wrapper.find('[data-secret-request]').exists()).toBe(false)
    wrapper.unmount()
  })
})

describe('answering in Settings → Secrets', () => {
  it('opens the form from the link, gives the saved secret to the session and drops the link', async () => {
    window.history.replaceState(null, '', '/settings/secrets?request=r1')
    const wrapper = mount(SecretsPane)
    await flushPromises()
    expect(wrapper.get('[data-secret-answering]').text()).toContain('Deploy bot')
    expect(wrapper.get('[data-secret-name]').element.value).toBe('SENTRY_DSN')
    expect(wrapper.get('[data-secret-description]').element.value).toBe('error reporting')
    await wrapper.get('[data-secret-value]').setValue('https://key@sentry.example.com/1')
    await wrapper.get('[data-secret-save]').trigger('click')
    await flushPromises()

    expect(mocks.api.upsertSecret).toHaveBeenCalledWith(expect.objectContaining({ name: 'SENTRY_DSN', value: 'https://key@sentry.example.com/1' }))
    expect(mocks.api.provideSecretRequest).toHaveBeenCalledWith('r1', 'sec1')
    expect(window.location.search).toBe('')
    expect(wrapper.get('[data-secret-granted]').text()).toContain('from its next start')
    expect(wrapper.html()).not.toContain('sentry.example.com/1')

    // A variable needs a new container; the restart is the owner's, in two steps.
    await wrapper.get('[data-secret-restart]').trigger('click')
    expect(mocks.api.pauseSession).not.toHaveBeenCalled()
    await wrapper.get('[data-secret-restart]').trigger('click')
    await flushPromises()
    expect(mocks.api.pauseSession).toHaveBeenCalledWith('s1')
    expect(mocks.api.resumeSession).toHaveBeenCalledWith('s1')
  })

  it('answers with an existing secret without retyping it', async () => {
    mocks.api.listSecrets.mockResolvedValue([{ id: 'old', name: 'SENTRY_DSN', delivery: 'env', description: null }])
    mocks.api.upsertSecret.mockResolvedValue({ id: 'old', name: 'SENTRY_DSN', delivery: 'env' })
    mocks.api.provideSecretRequest.mockResolvedValue({ secretId: 'old', name: 'SENTRY_DSN', delivery: 'env', restartRequired: false })
    const wrapper = mount(SecretsPane)
    await flushPromises()
    await wrapper.get('[data-secret-request-answer]').trigger('click')
    expect(wrapper.get('[data-secret-name]').attributes('readonly')).toBeDefined()
    await wrapper.get('[data-secret-save]').trigger('click')
    await flushPromises()
    expect(mocks.api.upsertSecret).toHaveBeenCalledWith(expect.objectContaining({ name: 'SENTRY_DSN', value: null }))
    expect(mocks.api.provideSecretRequest).toHaveBeenCalledWith('r1', 'old')
  })

  it('says so when the link points at a request that is already closed', async () => {
    window.history.replaceState(null, '', '/settings/secrets?request=r9')
    mocks.api.listSecretRequests.mockResolvedValue([])
    mocks.api.getSecretRequest.mockResolvedValue({ ...request, id: 'r9', status: 'expired' })
    const wrapper = mount(SecretsPane)
    await flushPromises()
    expect(wrapper.get('[data-secret-load-error]').text()).toContain('already been answered')
    expect(wrapper.find('[data-secret-form]').exists()).toBe(false)
  })
})
