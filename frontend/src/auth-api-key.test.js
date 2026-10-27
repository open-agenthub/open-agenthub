// @vitest-environment happy-dom
import { beforeEach, describe, expect, it, vi } from 'vitest'
import { mount, flushPromises } from '@vue/test-utils'

vi.mock('oidc-client-ts', () => ({ UserManager: vi.fn(), WebStorageStateStore: vi.fn() }))

const KEY = 'k'.repeat(48)

function configResponse(body) {
  return { ok: true, status: 200, json: () => Promise.resolve(body) }
}

async function freshAuth(cfg) {
  vi.resetModules()
  globalThis.fetch = vi.fn().mockResolvedValueOnce(configResponse(cfg))
  const mod = await import('./auth.js')
  await mod.initAuth()
  return mod
}

describe('API-key sign-in', () => {
  beforeEach(() => { sessionStorage.clear(); localStorage.clear() })

  it('needs the key before the app is usable and sends nothing until then', async () => {
    const { auth, getToken } = await freshAuth({ authMode: 'apiKey', authority: '' })
    expect(auth.enabled).toBe(true)
    expect(auth.isAuthenticated).toBe(false)
    expect(await getToken()).toBeNull()
  })

  it('checks the key against the backend and keeps it for the browser session only', async () => {
    const { auth, getToken, signInWithApiKey, API_KEY_STORAGE } = await freshAuth({ authMode: 'apiKey' })
    const check = vi.fn().mockResolvedValue({ ok: true, status: 200, json: () => Promise.resolve({ isAdmin: true, user: 'admin' }) })

    await signInWithApiKey(`  ${KEY}  `, check)

    expect(check).toHaveBeenCalledWith('/api/admin/access', { headers: { Authorization: `Bearer ${KEY}` } })
    expect(sessionStorage.getItem(API_KEY_STORAGE)).toBe(KEY)
    // localStorage would outlive the browser session and every later user of the profile.
    expect(Object.keys(localStorage)).toEqual([])
    expect(auth.isAuthenticated).toBe(true)
    expect(auth.user).toBe('admin')
    expect(await getToken()).toBe(KEY)
  })

  it('reports a wrong key on the prompt and stores nothing', async () => {
    const { auth, signInWithApiKey, API_KEY_STORAGE } = await freshAuth({ authMode: 'apiKey' })
    const check = vi.fn().mockResolvedValue({ ok: false, status: 401, json: () => Promise.resolve({}) })

    await expect(signInWithApiKey('wrong', check)).rejects.toThrow('not valid')
    expect(sessionStorage.getItem(API_KEY_STORAGE)).toBeNull()
    expect(auth.isAuthenticated).toBe(false)
  })

  it('picks up the key stored earlier in the same browser session', async () => {
    sessionStorage.setItem('agenthub.apiKey', KEY)
    sessionStorage.setItem('agenthub.apiKeyUser', 'admin')
    const { auth, getToken } = await freshAuth({ authMode: 'apiKey' })
    expect(auth.isAuthenticated).toBe(true)
    expect(await getToken()).toBe(KEY)
  })

  it('forgets the key on sign-out', async () => {
    sessionStorage.setItem('agenthub.apiKey', KEY)
    const { auth } = await freshAuth({ authMode: 'apiKey' })
    const reload = vi.fn()
    vi.stubGlobal('location', { ...window.location, reload })
    try {
      auth.logout()
    } finally { vi.unstubAllGlobals() }
    expect(sessionStorage.getItem('agenthub.apiKey')).toBeNull()
    expect(reload).toHaveBeenCalled()
  })

  it('flags the insecure development mode so the app can warn', async () => {
    const { auth, getToken } = await freshAuth({ authMode: 'insecureDev', authority: '' })
    expect(auth.insecure).toBe(true)
    expect(auth.enabled).toBe(false)
    expect(auth.isAuthenticated).toBe(true)
    expect(await getToken()).toBeNull()
  })

  it('treats a backend without authMode as before: no authority means the open dev mode', async () => {
    const { auth } = await freshAuth({ authority: '' })
    expect(auth.mode).toBe('insecureDev')
  })
})

describe('API key on the wire', () => {
  beforeEach(() => { sessionStorage.clear() })

  it('rides in the Authorization header on REST and in access_token on WebSockets', async () => {
    sessionStorage.setItem('agenthub.apiKey', KEY)
    await freshAuth({ authMode: 'apiKey' })
    const api = await import('./api.js')
    globalThis.fetch = vi.fn().mockResolvedValue({ ok: true, status: 200, json: () => Promise.resolve([]), text: () => Promise.resolve('[]') })

    await api.api.adminAccess()
    expect(fetch).toHaveBeenLastCalledWith('/api/admin/access', expect.objectContaining({
      headers: expect.objectContaining({ Authorization: `Bearer ${KEY}` })
    }))
    expect(await api.terminalUrl('s1')).toMatch(new RegExp(`/ws/sessions/s1/terminal\\?access_token=${KEY}$`))
  })
})

describe('ApiKeyLogin', () => {
  it('shows the error from a rejected key and emits nothing', async () => {
    const { default: ApiKeyLogin } = await import('./components/ApiKeyLogin.vue')
    const signIn = vi.fn().mockRejectedValue(new Error('That API key is not valid.'))
    const wrapper = mount(ApiKeyLogin, { props: { signIn } })

    await wrapper.find('[data-api-key-input]').setValue('nope')
    await wrapper.find('form').trigger('submit')
    await flushPromises()

    expect(signIn).toHaveBeenCalledWith('nope')
    expect(wrapper.find('[data-api-key-error]').text()).toBe('That API key is not valid.')
    expect(wrapper.emitted('signed-in')).toBeUndefined()
  })

  it('emits signed-in once the key is accepted', async () => {
    const { default: ApiKeyLogin } = await import('./components/ApiKeyLogin.vue')
    const signIn = vi.fn().mockResolvedValue()
    const wrapper = mount(ApiKeyLogin, { props: { signIn } })

    await wrapper.find('[data-api-key-input]').setValue(KEY)
    await wrapper.find('form').trigger('submit')
    await flushPromises()

    expect(wrapper.emitted('signed-in')).toHaveLength(1)
    expect(wrapper.find('input').attributes('type')).toBe('password')
  })
})
