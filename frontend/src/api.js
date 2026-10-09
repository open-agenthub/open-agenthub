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
async function uploadSessionFile(upload, body, options = {}) {
  if (!upload?.url || !['proxy', 'presigned'].includes(upload.kind))
    throw new Error('Invalid upload descriptor')
  const headers = { ...(upload.headers || {}) }
  if (upload.kind === 'proxy') Object.assign(headers, await authHeaders())
  const init = { method: 'PUT', headers, body }
  if (options.signal) init.signal = options.signal
  const res = await fetch(upload.url, init)
  if (res.status === 401 && upload.kind === 'proxy') handle401()
  if (!res.ok) {
    const err = new Error(`${res.status} ${await res.text()}`)
    err.status = res.status
    throw err
  }
  return null
}

async function fileContent(path, authenticated) {
  const res = await fetch(`/api${path}`, {
    method: 'GET',
    headers: authenticated ? await authHeaders() : {}
  })
  if (res.status === 401 && authenticated) handle401()
  if (!res.ok) {
    const error = new Error(`${res.status} ${await res.text()}`)
    error.status = res.status
    throw error
  }
  return res.blob()
}

async function sharedReq(path) {
  const res = await fetch(`/api${path}`, { method: 'GET', headers: {} })
  if (!res.ok) {
    const err = new Error(`${res.status} ${await res.text()}`)
    err.status = res.status
    throw err
  }
  return res.json()
}


export const api = {
  listSessions: () => req('GET', '/sessions'),
  getSession: (id) => req('GET', `/sessions/${id}`),
  sessionFileCapabilities: (id) => req('GET', `/sessions/${encodeURIComponent(id)}/files/capabilities`),
  reserveSessionFile: (id, data) => req('POST', `/sessions/${encodeURIComponent(id)}/files/reserve`, data),
  uploadSessionFile,
  completeSessionFile: (id, fileId) => req('POST', `/sessions/${encodeURIComponent(id)}/files/${encodeURIComponent(fileId)}/complete`),
  listSessionFiles: (id) => req('GET', `/sessions/${encodeURIComponent(id)}/files`),
  sessionFileContentUrl: (id, fileId) => `/api/sessions/${encodeURIComponent(id)}/files/${encodeURIComponent(fileId)}/content`,
  deleteSessionFile: (id, fileId) => req('DELETE', `/sessions/${encodeURIComponent(id)}/files/${encodeURIComponent(fileId)}`),
  getFilePresentation: (id) => req('GET', `/sessions/${encodeURIComponent(id)}/files/presentation`),
  setFilePresentation: (id, fileId) => req('PUT', `/sessions/${encodeURIComponent(id)}/files/presentation`, { fileId }),
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
  // Recent agent-to-agent messages sent to a session (read-only fleet inbox view).
  listSessionMessages: (id) => req('GET', `/sessions/${encodeURIComponent(id)}/messages`),
  // Pending tool-permission requests + in-app approval (mirrors the messenger buttons).
  listPermissions: (id) => req('GET', `/sessions/${encodeURIComponent(id)}/permissions`),
  decidePermission: (id, reqId, decision) => req('POST', `/sessions/${encodeURIComponent(id)}/permissions/${encodeURIComponent(reqId)}`, { decision }),
  deleteSession: (id) => req('DELETE', `/sessions/${id}`),
  storeCredentials: (data) => req('PUT', '/credentials', data),
  // Which credential fields have a stored value (booleans only, never values) plus the git
  // token list as {id, kind, host} — never the tokens.
  getCredentialStatus: () => req('GET', '/credentials'),
  // Git PATs are a list keyed by host, not merge fields of storeCredentials: adding one for a
  // host that is already stored rotates that entry.
  addGitPat: (data) => req('POST', '/credentials/git-pats', data),
  deleteGitPat: (id) => req('DELETE', `/credentials/git-pats/${encodeURIComponent(id)}`),
  // A subscription login is captured from a session by its runtime rather than typed in here,
  // so this is the only way to get rid of one that stopped working.
  deleteSubscriptionCredential: (agent) =>
    req('DELETE', `/credentials/subscription/${encodeURIComponent(agent)}`),
  // GDPR: irreversibly deletes the caller's account and all its data.
  deleteAccount: (confirm) => req('DELETE', `/account?confirm=${encodeURIComponent(confirm)}`),
  // Personal API tokens for driving sessions remotely.
  listApiTokens: () => req('GET', '/tokens'),
  // Returns the plaintext token exactly once (in the `token` field).
  createApiToken: (name) => req('POST', '/tokens', { name }),
  deleteApiToken: (id) => req('DELETE', `/tokens/${id}`),
  // Token/cost usage dashboard (fed by the agents' OpenTelemetry exporter).
  usageSummary: () => req('GET', '/usage/summary'),
  usageSessions: () => req('GET', '/usage/sessions'),
  // Personal monthly API budget (community feature).
  usageLimit: () => req('GET', '/usage/limit'),
  setUsageLimit: (limitUsd) => req('PUT', '/usage/limit', { limitUsd }),
  // Enterprise admin: usage limits (global/group/user) + group→role mapping. 402 without license.
  eeListLimits: () => req('GET', '/ee/admin/limits'),
  eeSetLimit: (data) => req('PUT', '/ee/admin/limits', data),
  eeDeleteLimit: (scope, target) => req('DELETE', `/ee/admin/limits/${encodeURIComponent(scope)}${target ? `?target=${encodeURIComponent(target)}` : ''}`),
  eeListGroups: () => req('GET', '/ee/admin/groups'),
  eeSetGroupRole: (group, role) => req('PUT', `/ee/admin/groups/${encodeURIComponent(group)}/role`, { role }),
  // Effective allowlist for session selectors (empty store expands to all agents server-side).
  getAllowedAgents: () => req('GET', '/agents/allowed'),
  // Enterprise admin: raw allowlist (empty = unrestricted). 402 without license; 403 if not admin.
  adminGetAllowedAgents: () => req('GET', '/admin/allowed-agents'),
  adminSetAllowedAgents: (agents) => req('PUT', '/admin/allowed-agents', { agents }),
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
  // Self-service activation: pull this instance's token from the service by billing email
  // (matched against the instance key sent at checkout). Recovers a lost redirect/email.
  claimLicense: (email) => req('POST', '/admin/license/claim', { email }),
  // Fresh Stripe billing-portal session for the licensed email; returns { url }.
  openBillingPortal: () => req('POST', '/admin/billing-portal'),
  deactivateLicense: () => req('DELETE', '/admin/license'),
  setUserSeat: (owner, licensed) => req('PUT', `/admin/users/${encodeURIComponent(owner)}/license`, { licensed }),
  // Personal library: MCP catalog (raw/api) + skills.
  mcpServers: () => req('GET', '/mcp-servers'),
  createMcpServer: (data) => req('POST', '/mcp-servers', data),
  createMcpServerFromApi: (data) => req('POST', '/mcp-servers/from-api', data),
  updateMcpServer: (id, data) => req('PUT', `/mcp-servers/${encodeURIComponent(id)}`, data),
  deleteMcpServer: (id) => req('DELETE', `/mcp-servers/${encodeURIComponent(id)}`),
  // Admin org MCP catalog (owner=__org__). Not license-gated.
  adminMcpServers: () => req('GET', '/admin/mcp-servers'),
  createAdminMcpServer: (data) => req('POST', '/admin/mcp-servers', data),
  updateAdminMcpServer: (id, data) => req('PUT', `/admin/mcp-servers/${encodeURIComponent(id)}`, data),
  deleteAdminMcpServer: (id) => req('DELETE', `/admin/mcp-servers/${encodeURIComponent(id)}`),
  skills: () => req('GET', '/skills'),
  skill: (id) => req('GET', `/skills/${encodeURIComponent(id)}`),
  createSkill: (data) => req('POST', '/skills', data),
  updateSkill: (id, data) => req('PUT', `/skills/${encodeURIComponent(id)}`, data),
  deleteSkill: (id) => req('DELETE', `/skills/${encodeURIComponent(id)}`),
  // Hybrid skill search (FTS + optional vector similarity on the server).
  searchSkills: (q, projectId) => req('GET', `/skills/search?q=${encodeURIComponent(q)}`
    + (projectId != null ? `&projectId=${encodeURIComponent(projectId)}` : '')),
  skillVersions: (id) => req('GET', `/skills/${encodeURIComponent(id)}/versions`),
  skillVersion: (id, version) => req('GET', `/skills/${encodeURIComponent(id)}/versions/${version}`),
  restoreSkillVersion: (id, version) => req('POST', `/skills/${encodeURIComponent(id)}/restore`, { version }),
  // Enterprise library sharing — throw with .status 402 without a license.
  // Known users for the sharing pickers (admin-only).
  libraryUsers: () => req('GET', '/ee/library/users'),
  librarySettings: () => req('GET', '/ee/library/settings'),
  setLibrarySettings: (data) => req('PUT', '/ee/library/settings', data),
  // kind: 'mcp-servers' | 'skills'
  libraryShares: (kind, id) => req('GET', `/ee/library/${kind}/${encodeURIComponent(id)}/shares`),
  setLibraryShares: (kind, id, data) => req('PUT', `/ee/library/${kind}/${encodeURIComponent(id)}/shares`, data),
  // Webhook triggers: inbound GitLab/GitHub MR/PR webhooks start autonomous sessions.
  listWebhookTriggers: () => req('GET', '/webhook-triggers'),
  // Returns { trigger, secret } — the plaintext secret is shown exactly once.
  createWebhookTrigger: (data) => req('POST', '/webhook-triggers', data),
  deleteWebhookTrigger: (id) => req('DELETE', `/webhook-triggers/${encodeURIComponent(id)}`),
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
export const getSharedFileCapabilities = (token) => sharedReq(`/shared/${encodeURIComponent(token)}/files/capabilities`)
export const listSharedSessionFiles = (token) => sharedReq(`/shared/${encodeURIComponent(token)}/files`)
export const getSharedFilePresentation = (token) => sharedReq(`/shared/${encodeURIComponent(token)}/files/presentation`)
export const sharedSessionFileContentUrl = (token, fileId) => `/api/shared/${encodeURIComponent(token)}/files/${encodeURIComponent(fileId)}/content`
export const getSessionFileContent = (id, fileId) =>
  fileContent(`/sessions/${encodeURIComponent(id)}/files/${encodeURIComponent(fileId)}/content`, true)
export const getSharedSessionFileContent = (token, fileId) =>
  fileContent(`/shared/${encodeURIComponent(token)}/files/${encodeURIComponent(fileId)}/content`, false)
export const sharedTerminalUrl = (token) => `${location.protocol === 'https:' ? 'wss' : 'ws'}://${location.host}/ws/shared/${encodeURIComponent(token)}/terminal`

// WebSocket URL including the token (browser WebSockets cannot set headers).
async function wsUrl(id, kind) {
  const proto = location.protocol === 'https:' ? 'wss' : 'ws'
  const t = await getToken()
  const q = t ? `?access_token=${encodeURIComponent(t)}` : ''
  return `${proto}://${location.host}/ws/sessions/${encodeURIComponent(id)}/${kind}${q}`
}

// The selected agent's shared terminal.
export const terminalUrl = (id) => wsUrl(id, 'terminal')
export const resizeBrowserViewport = (id, width, height) => req('PUT', `/sessions/${encodeURIComponent(id)}/browser/viewport`, { width, height })
export const browserUrl = (id) => wsUrl(id, 'browser')
// Server push for "something about this session changed". Async like the other session sockets
// because the access token travels in the query string.
export const sessionEventsUrl = (id) => wsUrl(id, 'events')
export const sharedSessionEventsUrl = (token) => `${location.protocol === 'https:' ? 'wss' : 'ws'}://${location.host}/ws/shared/${encodeURIComponent(token)}/events`
export const sharedBrowserUrl = (token) => `${location.protocol === 'https:' ? 'wss' : 'ws'}://${location.host}/ws/shared/${encodeURIComponent(token)}/browser`
export async function getSharedSession(token) {
  const res = await fetch(`/api/shared/${encodeURIComponent(token)}/session`)
  if (!res.ok) {
    const error = new Error(`${res.status} ${await res.text()}`)
    error.status = res.status
    throw error
  }
  return res.json()
}
export async function getSharedTranscript(token) {
  const res = await fetch(`/api/shared/${encodeURIComponent(token)}/transcript`)
  if (!res.ok) throw new Error(`${res.status} ${await res.text()}`)
  return res.text()
}

// An interactive bash shell in the same pod.
export const shellUrl = (id) => wsUrl(id, 'shell')
