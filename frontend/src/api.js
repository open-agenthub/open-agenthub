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
  if (!res.ok) throw new Error(`${res.status} ${await res.text()}`)
  return res.status === 204 ? null : res.json()
}

export const api = {
  listSessions: () => req('GET', '/sessions'),
  getSession: (id) => req('GET', `/sessions/${id}`),
  createSession: (data) => req('POST', '/sessions', data),
  updateSession: (id, data) => req('PATCH', `/sessions/${id}`, data),
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
  // Admin area: license activation (stored in DB) + seat management.
  adminAccess: () => req('GET', '/admin/access'),
  adminOverview: () => req('GET', '/admin/overview'),
  activateLicense: (token) => req('POST', '/admin/license', { token }),
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

// WebSocket URL including the token (browser WebSockets cannot set headers).
async function wsUrl(id, kind) {
  const proto = location.protocol === 'https:' ? 'wss' : 'ws'
  const t = await getToken()
  const q = t ? `?access_token=${encodeURIComponent(t)}` : ''
  return `${proto}://${location.host}/ws/sessions/${id}/${kind}${q}`
}

// The shared Claude terminal.
export const terminalUrl = (id) => wsUrl(id, 'terminal')
// An interactive bash shell in the same pod.
export const shellUrl = (id) => wsUrl(id, 'shell')
