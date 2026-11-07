import { chromium } from '../../browser-runtime/node_modules/playwright-core/index.mjs'
const browser = await chromium.launch({ headless: true, args: ['--no-sandbox'] })
try {
  const page = await browser.newPage()
  await page.goto('http://127.0.0.1:18080/')
  await page.getByRole('button', { name: /sign in/i }).first().click()
  await page.locator('#username').fill('team-admin')
  await page.locator('#password').fill(process.env.PROOF_LOGIN_PASSWORD)
  await page.locator('#kc-login').click()
  await page.waitForURL('http://127.0.0.1:18080/**', { timeout: 60000 })
  await page.waitForFunction(() => Object.keys(localStorage).some(key => key.startsWith('oidc.user:')))
  const result = await page.evaluate(async issuer => {
    const key = Object.keys(localStorage).find(key => key.startsWith('oidc.user:'))
    const user = JSON.parse(localStorage.getItem(key))
    if (!user.access_token || !user.refresh_token) throw new Error('Missing code-flow or offline token')
    const access = await fetch('/api/admin/access', { headers: { Authorization: `Bearer ${user.access_token}` } })
    if (!access.ok) throw new Error('Initial API request failed')
    const identity = await access.json()
    const renewed = await fetch(`${issuer}/protocol/openid-connect/token`, {
      method: 'POST', body: new URLSearchParams({ grant_type: 'refresh_token', client_id: 'agenthub', refresh_token: user.refresh_token })
    })
    if (!renewed.ok) throw new Error('Offline refresh failed')
    const tokens = await renewed.json()
    const api = await fetch('/api/admin/overview', { headers: { Authorization: `Bearer ${tokens.access_token}` } })
    return { user: identity.user, isAdmin: identity.isAdmin, refreshed: api.ok && (await api.json()).isAdmin }
  }, process.env.PROOF_ISSUER)
  if (result.user !== 'team-admin' || !result.isAdmin || !result.refreshed) throw new Error('Team login or refreshed admin identity failed')
  console.log('PASS: real identity-service UI, production frontend PKCE callback, offline refresh and authenticated team-admin API')
} finally { await browser.close() }
