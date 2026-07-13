import { UserManager, WebStorageStateStore } from 'oidc-client-ts'
import { reactive } from 'vue'

// Generic OIDC sign-in (Authorization Code + PKCE) via oidc-client-ts.
// The provider config is fetched at RUNTIME from the backend (GET /api/config), because
// the frontend is deployed as a static nginx image – no build-time env vars.
// Empty authority = "auth disabled" mode (local development, no login).

let manager = null
let user = null
let enabled = false

// Runtime backend config (e.g. whether Git OAuth providers / Slack are configured).
export const config = reactive({ gitEnabled: false, slackEnabled: false })

export async function initAuth() {
  let cfg = { authority: '' }
  try { cfg = await (await fetch('/api/config')).json() }
  catch (e) { console.error('Could not load auth config (/api/config) – auth disabled', e) }
  config.gitEnabled = !!cfg.gitEnabled
  config.slackEnabled = !!cfg.slackEnabled
  enabled = !!cfg.authority
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
