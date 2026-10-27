import { UserManager, WebStorageStateStore } from 'oidc-client-ts'
import { reactive } from 'vue'
import { DEV_VERSION, REPO_URL } from './lib/docs.js'

// Sign-in, chosen at RUNTIME from the backend (GET /api/config) because the frontend is deployed
// as a static nginx image – no build-time env vars. authMode is one of:
//   oidc         generic OIDC (Authorization Code + PKCE) via oidc-client-ts
//   apiKey       the install's static API key, asked for once per browser session
//   insecureDev  no login at all (local development only); the app shows a warning banner

// sessionStorage, not localStorage: the key is a long-lived admin credential, so it should end
// with the browser session instead of sitting on disk for every later user of the profile.
export const API_KEY_STORAGE = 'agenthub.apiKey'
const API_KEY_USER_STORAGE = 'agenthub.apiKeyUser'

let manager = null
let user = null
let enabled = false
let mode = 'insecureDev'
let apiKey = null
let apiKeyUser = null

// Runtime backend config (e.g. whether Git OAuth providers / Slack are configured).
// `version` starts as the value baked into this bundle so the footer has a label before
// /api/config answers; the backend's version replaces it, since image.tag moves every
// component together and the backend is the one that actually runs the deployment.
export const config = reactive({
  gitEnabled: false, slackEnabled: false, telegramEnabled: false, signalEnabled: false,
  version: import.meta.env.VITE_APP_VERSION || DEV_VERSION,
  repoUrl: REPO_URL
})

export async function initAuth() {
  let cfg = { authority: '' }
  try { cfg = await (await fetch('/api/config')).json() }
  catch (e) { console.error('Could not load auth config (/api/config) – auth disabled', e) }
  config.gitEnabled = !!cfg.gitEnabled
  config.slackEnabled = !!cfg.slackEnabled
  config.telegramEnabled = !!cfg.telegramEnabled
  config.signalEnabled = !!cfg.signalEnabled
  if (cfg.version) config.version = String(cfg.version)
  if (cfg.repoUrl) config.repoUrl = String(cfg.repoUrl)
  // A backend older than authMode only knows OIDC or the open "dev" mode.
  mode = cfg.authMode || (cfg.authority ? 'oidc' : 'insecureDev')
  if (mode === 'apiKey') {
    enabled = true
    try {
      apiKey = sessionStorage.getItem(API_KEY_STORAGE)
      apiKeyUser = sessionStorage.getItem(API_KEY_USER_STORAGE)
    } catch { apiKey = null }
    return
  }
  enabled = mode === 'oidc' && !!cfg.authority
  if (!enabled) return

  // Request offline_access so we get a refresh token (needed to survive long idle
  // periods / overnight without a redirect).
  const scope = cfg.scope || 'openid profile email'
  manager = new UserManager({
    authority: cfg.authority,
    client_id: cfg.clientId,
    scope: /\boffline_access\b/.test(scope) ? scope : `${scope} offline_access`,
    response_type: 'code',                                 // code flow; PKCE (S256) is the default
    redirect_uri: `${location.origin}/auth/callback`,
    post_logout_redirect_uri: location.origin,
    automaticSilentRenew: true,                            // renews via refresh token before expiry
    accessTokenExpiringNotificationTimeInSeconds: 120,     // start renewing 2 min before expiry
    userStore: new WebStorageStateStore({ store: localStorage })
  })
  manager.events.addUserLoaded(u => { user = u })
  manager.events.addUserSignedOut(() => { user = null })
  manager.events.addSilentRenewError(e => console.error('Silent renew failed', e))

  // Belt-and-braces: proactively refresh every 4 minutes so the token never lapses
  // while the tab sits idle (automaticSilentRenew alone can miss after long sleep).
  setInterval(() => { if (user) manager.signinSilent().then(u => { if (u) user = u }).catch(() => {}) }, 4 * 60 * 1000)

  // Returning from the provider: exchange the code for tokens, then clean up the URL.
  const q = new URLSearchParams(location.search)
  if (location.pathname === '/auth/callback' && q.has('state')) {
    try {
      user = await manager.signinRedirectCallback()
      history.replaceState(null, '', user.state?.returnTo || '/')
    } catch (e) {
      console.error('OIDC callback failed', e)
      history.replaceState(null, '', '/')
    }
    return
  }

  // Load an existing session from storage; silently renew expired ones.
  user = await manager.getUser()
  if (user?.expired) user = await manager.signinSilent().catch(() => null)
}

export function login() {
  // API-key mode: forget the key that was just rejected; the reload shows the key prompt.
  if (mode === 'apiKey') { forgetApiKey(); location.reload(); return }
  return manager?.signinRedirect({ state: { returnTo: location.pathname + location.search } })
}

function forgetApiKey() {
  apiKey = null
  apiKeyUser = null
  try { sessionStorage.removeItem(API_KEY_STORAGE); sessionStorage.removeItem(API_KEY_USER_STORAGE) } catch {}
}

// Checks a key against an authenticated endpoint before keeping it, so a typo is reported on the
// prompt instead of surfacing as a 401 from whichever page loads first. Throws on a wrong key.
export async function signInWithApiKey(key, fetchImpl = fetch) {
  const candidate = String(key || '').trim()
  if (!candidate) throw new Error('Enter the API key.')
  const res = await fetchImpl('/api/admin/access', { headers: { Authorization: `Bearer ${candidate}` } })
  if (res.status === 401) throw new Error('That API key is not valid.')
  if (!res.ok) throw new Error(`Could not check the key (HTTP ${res.status}).`)
  const body = await res.json().catch(() => ({}))
  apiKey = candidate
  apiKeyUser = body.user || null
  try {
    sessionStorage.setItem(API_KEY_STORAGE, candidate)
    if (apiKeyUser) sessionStorage.setItem(API_KEY_USER_STORAGE, apiKeyUser)
  } catch {}
}

// Return a valid access token; silently renew shortly before expiry.
export async function getToken() {
  if (mode === 'apiKey') return apiKey
  if (!enabled) return null
  if (user && (user.expired || (user.expires_in != null && user.expires_in < 30)))
    user = await manager.signinSilent().catch(() => user)
  return user?.access_token ?? null
}

export const auth = {
  get enabled() { return enabled },
  get mode() { return mode },
  get insecure() { return mode === 'insecureDev' },
  get isAuthenticated() {
    if (mode === 'apiKey') return !!apiKey
    return !enabled || (!!user && !user.expired)
  },
  get user() {
    if (mode === 'apiKey') return apiKeyUser ?? '–'
    return enabled ? (user?.profile?.preferred_username ?? '–') : 'dev'
  },
  get displayName() { return mode === 'oidc' && enabled ? (user?.profile?.name ?? this.user) : this.user },
  get email() { return mode === 'oidc' && enabled ? (user?.profile?.email ?? null) : null },
  login,
  logout: () => {
    if (mode === 'apiKey') { forgetApiKey(); location.reload(); return }
    return manager?.signoutRedirect()
  }
}
