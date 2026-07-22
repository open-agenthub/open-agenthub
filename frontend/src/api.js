import { auth, getToken, login } from './auth.js'
export { initAuth, auth, config } from './auth.js'

// Bearer header from the current access token (empty in "auth disabled" mode).
async function authHeaders() {
  const t = await getToken()
  return t ? { Authorization: `Bearer ${t}` } : {}
}

// On 401, go back to the provider – the session has expired or the token is invalid.
function handle401() {
  if (auth.enabled) login()
  throw new Error('401 Sign-in required')
}

async function req(method, path, body) {
  const res = await fetch(`/api${path}`, {
    method,
    headers: { 'Content-Type': 'application/json', ...(await authHeaders()) },
    body: body ? JSON.stringify(body) : undefined
  })
  if (res.status === 401) handle401()
  if (!res.ok) {
    // `.status` lets callers map specific failures (e.g. 503) to inline messages.
    const err = new Error(`${res.status} ${await res.text()}`)
    err.status = res.status
    throw err
  }
  return res.status === 204 ? null : res.json()
}

// Like req(), but resolves to the HTTP status code (the caller distinguishes
// e.g. 202 "verification sent" from 204 "saved") and throws an Error carrying
// `.status` so failures (400/409/429/503) can be mapped to inline messages.
async function reqStatus(method, path, body) {
  const res = await fetch(`/api${path}`, {
    method,
    headers: { 'Content-Type': 'application/json', ...(await authHeaders()) },
    body: body ? JSON.stringify(body) : undefined
  })
  if (res.status === 401) handle401()
  if (!res.ok) {
    const err = new Error(`${res.status} ${await res.text()}`)
    err.status = res.status
    throw err
  }
  return res.status
}

export const api = {
  listSessions: () => req('GET', '/sessions'),
  getSession: (id) => req('GET', `/sessions/${id}`),
  createSession: (data) => req('POST', '/sessions', data),
  updateSession: (id, data) => req('PATCH', `/sessions/${id}`, data),
  duplicateSession: (id, data) => req('POST', `/sessions/${encodeURIComponent(id)}/duplicate`, data),
  listProjects: () => req('GET', '/projects'),
  createProject: (data) => req('POST', '/projects', data),
  updateProject: (id, data) => req('PATCH', `/projects/${encodeURIComponent(id)}`, data),
  deleteProject: (id) => req('DELETE', `/projects/${encodeURIComponent(id)}`),
  listSessionShares: (id) => req('GET', `/ee/sessions/${encodeURIComponent(id)}/shares`),
  createShareUser: (id, data) => req('POST', `/ee/sessions/${encodeURIComponent(id)}/shares/users`, data),
  updateShareUser: (id, recipient, data) => req('PATCH', `/ee/sessions/${encodeURIComponent(id)}/shares/users/${encodeURIComponent(recipient)}`, data),
  deleteShareUser: (id, recipient) => req('DELETE', `/ee/sessions/${encodeURIComponent(id)}/shares/users/${encodeURIComponent(recipient)}`),
  createShareLink: (id, data) => req('POST', `/ee/sessions/${encodeURIComponent(id)}/shares/links`, data),
  updateShareLink: (id, linkId, data) => req('PATCH', `/ee/sessions/${encodeURIComponent(id)}/shares/links/${encodeURIComponent(linkId)}`, data),
  deleteShareLink: (id, linkId) => req('DELETE', `/ee/sessions/${encodeURIComponent(id)}/shares/links/${encodeURIComponent(linkId)}`),
  updateMcpPolicy: (id, data) => req('PUT', `/ee/sessions/${encodeURIComponent(id)}/mcp-policy`, data),
  resumeSession: (id) => req('POST', `/sessions/${id}/resume`),
  pauseSession: (id) => req('POST', `/sessions/${id}/pause`),
  deleteSession: (id) => req('DELETE', `/sessions/${id}`),
  storeCredentials: (data) => req('PUT', '/credentials', data),
  // Which credential fields have a stored value (booleans only, never values).
  getCredentialStatus: () => req('GET', '/credentials'),
  // Personal API tokens for driving sessions remotely.
  listApiTokens: () => req('GET', '/tokens'),
  // Returns the plaintext token exactly once (in the `token` field).
  createApiToken: (name) => req('POST', '/tokens', { name }),
  deleteApiToken: (id) => req('DELETE', `/tokens/${id}`),
  // Token/cost usage dashboard (fed by the agents' OpenTelemetry exporter).
  usageSummary: () => req('GET', '/usage/summary'),
  usageSessions: () => req('GET', '/usage/sessions'),
  // Per-user Slack preferences.
  slackMe: () => req('GET', '/slack/me'),
  setSlackPrefs: (data) => req('PUT', '/slack/me', data),
  // Per-user Telegram/Signal chat settings.
  chatMe: () => req('GET', '/chat/me'),
  telegramLinkCode: () => req('POST', '/chat/telegram/link-code'),
  setTelegramPrefs: (data) => req('PUT', '/chat/telegram', data),
  unlinkTelegram: () => req('DELETE', '/chat/telegram'),
  // 204 = saved, 202 = verification code sent; throws with .status on 400/409/503.
  setSignalPrefs: (data) => reqStatus('PUT', '/chat/signal', data),
  // 204 = verified; throws with .status on 400 (invalid/expired) / 429 (too many attempts).
  verifySignal: (code) => reqStatus('POST', '/chat/signal/verify', { code }),
  // Admin area: license activation (stored in DB) + seat management.
  adminAccess: () => req('GET', '/admin/access'),
  adminOverview: () => req('GET', '/admin/overview'),
  activateLicense: (token) => req('POST', '/admin/license', { token }),
  // Starts a Stripe checkout on the license service; returns { url } to redirect to.
  startLicenseCheckout: (data) => req('POST', '/admin/license/checkout', data),
  deactivateLicense: () => req('DELETE', '/admin/license'),
  setUserSeat: (owner, licensed) => req('PUT', `/admin/users/${encodeURIComponent(owner)}/license`, { licensed }),
  // Git OAuth providers / connections.
  gitProviders: () => req('GET', '/git/providers'),
  gitConnectUrl: (providerId) => req('GET', `/git/connect/${providerId}`),
  gitDisconnect: (providerId) => req('DELETE', `/git/connections/${providerId}`),
  gitProjects: (provider, q) => req('GET', `/git/projects?provider=${encodeURIComponent(provider)}&q=${encodeURIComponent(q || '')}`),
  async getTranscript(id) {
    const res = await fetch(`/api/sessions/${id}/transcript`, { headers: await authHeaders() })
    if (res.status === 401) handle401()
    return res.ok ? res.text() : ''
  }
}
export const sharedTerminalUrl = (token) => `${location.protocol === 'https:' ? 'wss' : 'ws'}://${location.host}/ws/shared/${encodeURIComponent(token)}/terminal`

// WebSocket URL including the token (browser WebSockets cannot set headers).
async function wsUrl(id, kind) {
  const proto = location.protocol === 'https:' ? 'wss' : 'ws'
  const t = await getToken()
  const q = t ? `?access_token=${encodeURIComponent(t)}` : ''
  return `${proto}://${location.host}/ws/sessions/${id}/${kind}${q}`
}

// The shared Claude terminal.
export const terminalUrl = (id) => wsUrl(id, 'terminal')
export async function getSharedSession(token) {
  const res = await fetch(`/api/shared/${encodeURIComponent(token)}/session`)
  if (!res.ok) throw new Error(`${res.status} ${await res.text()}`)
  return res.json()
}

export async function getSharedTranscript(token) {
  const res = await fetch(`/api/shared/${encodeURIComponent(token)}/transcript`)
  if (!res.ok) throw new Error(`${res.status} ${await res.text()}`)
  return res.text()
}

// An interactive bash shell in the same pod.
export const shellUrl = (id) => wsUrl(id, 'shell')
