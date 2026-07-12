import { auth, getToken, login } from './auth.js'
export { initAuth, auth } from './auth.js'

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
  deleteSession: (id) => req('DELETE', `/sessions/${id}`),
  storeCredentials: (data) => req('PUT', '/credentials', data),
  // Which credential fields have a stored value (booleans only, never values).
  getCredentialStatus: () => req('GET', '/credentials'),
  async getTranscript(id) {
    const res = await fetch(`/api/sessions/${id}/transcript`, { headers: await authHeaders() })
    if (res.status === 401) handle401()
    return res.ok ? res.text() : ''
  }
}

// WebSocket URL including the token (browser WebSockets cannot set headers).
export async function terminalUrl(id) {
  const proto = location.protocol === 'https:' ? 'wss' : 'ws'
  const t = await getToken()
  const q = t ? `?access_token=${encodeURIComponent(t)}` : ''
  return `${proto}://${location.host}/ws/sessions/${id}/terminal${q}`
}
