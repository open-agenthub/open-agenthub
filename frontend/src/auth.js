import { UserManager, WebStorageStateStore } from 'oidc-client-ts'
import { reactive } from 'vue'

// Generic OIDC sign-in (Authorization Code + PKCE) via oidc-client-ts.
// The provider config is fetched at RUNTIME from the backend (GET /api/config), because
// the frontend is deployed as a static nginx image – no build-time env vars.
// Empty authority = "auth disabled" mode (local development, no login).

let manager = null
let user = null
let enabled = false

// Runtime backend config (e.g. whether Git OAuth providers are configured).
export const config = reactive({ gitEnabled: false })

export async function initAuth() {
  let cfg = { authority: '' }
  try { cfg = await (await fetch('/api/config')).json() }
  catch (e) { console.error('Could not load auth config (/api/config) – auth disabled', e) }
  config.gitEnabled = !!cfg.gitEnabled
  enabled = !!cfg.authority
  if (!enabled) return

  manager = new UserManager({
    authority: cfg.authority,
    client_id: cfg.clientId,
    scope: cfg.scope || 'openid profile email',
    response_type: 'code',                                 // code flow; PKCE (S256) is the default
    redirect_uri: `${location.origin}/auth/callback`,
    post_logout_redirect_uri: location.origin,
    automaticSilentRenew: true,                            // renews via refresh token before expiry
    userStore: new WebStorageStateStore({ store: localStorage })
  })
  manager.events.addUserLoaded(u => { user = u })
  manager.events.addUserSignedOut(() => { user = null })
  manager.events.addSilentRenewError(e => console.error('Silent renew failed', e))

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
  return manager?.signinRedirect({ state: { returnTo: location.pathname + location.search } })
}

// Return a valid access token; silently renew shortly before expiry.
export async function getToken() {
  if (!enabled) return null
  if (user && (user.expired || (user.expires_in != null && user.expires_in < 30)))
    user = await manager.signinSilent().catch(() => user)
  return user?.access_token ?? null
}

export const auth = {
  get enabled() { return enabled },
  get isAuthenticated() { return !enabled || (!!user && !user.expired) },
  get user() { return enabled ? (user?.profile?.preferred_username ?? '–') : 'dev' },
  login,
  logout: () => manager?.signoutRedirect()
}
