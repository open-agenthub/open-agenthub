// #427 in Chromium: a link holder opens the shared view of a long running session, pages back
// to the first review (older than the stored Postgres tail), sees no callback token or MCP
// credential, and loses the history when the link is deleted or expires; a named Viewer reads
// the same history and loses it on unshare. Runs against Program.cs + Vite; see README.md.
// Env: UI (default http://localhost:5173), PW_MODULE (playwright-core), CHROME, OUT (screenshots).
const { chromium } = require(process.env.PW_MODULE || 'playwright-core')
const UI = process.env.UI || 'http://localhost:5173'
const OUT = process.env.OUT || '.'
const FIRST = 'Review 1 of today: approved the parser change'
const TOKEN = 'callback-token-reviews-7f3c9e'
const MCP = 'mcp-owner-credential-5b1d2a'
const results = []
const check = (name, ok, detail = '') => { results.push({ name, ok: !!ok, detail }); console.log(`${ok ? 'PASS' : 'FAIL'} ${name}${detail ? ' — ' + detail : ''}`) }

async function context(browser, user) {
  const ctx = await browser.newContext({ viewport: { width: 1280, height: 900 } })
  if (user) await ctx.addCookies([{ name: 'proof-user', value: user, url: UI }])
  return ctx
}
const api = (page, method, path, data) => page.request.fetch(UI + path, { method, data })

;(async () => {
  const browser = await chromium.launch({ executablePath: process.env.CHROME, args: ['--no-sandbox', '--disable-dev-shm-usage'] })
  const alice = await (await context(browser, 'alice')).newPage()
  const bob = await (await context(browser, 'bob')).newPage()
  await alice.goto(UI + '/api/config')

  const stored = await (await api(alice, 'GET', '/api/proof/stored-has-first')).json()
  check('the stored copy (Postgres tail) does not contain the first review', stored === false)

  const viewerLink = await (await api(alice, 'POST', '/api/ee/sessions/reviews/shares/links', { role: 'Viewer', expiresAt: null })).json()
  const collabLink = await (await api(alice, 'POST', '/api/ee/sessions/reviews/shares/links',
    { role: 'Collaborator', expiresAt: new Date(Date.now() + 3600_000).toISOString() })).json()
  const shared = await api(alice, 'POST', '/api/ee/sessions/reviews/shares/users', { recipient: 'bob', role: 'Viewer' })
  check('alice creates a Viewer link, an expiring Collaborator link and a named Viewer share', viewerLink.url && collabLink.url && shared.ok())
  const tokenOf = link => link.url.slice(link.url.lastIndexOf('/') + 1)

  // The link holder: no cookie at all.
  const holderContext = await context(browser, null)
  const holder = await holderContext.newPage()
  const errors = []
  holder.on('pageerror', e => errors.push(String(e)))
  await holder.goto(`${UI}/shared/${tokenOf(viewerLink)}`)
  await holder.waitForSelector('[data-workspace-role]', { timeout: 30_000 })
  const lastText = await holder.$$eval('[data-workspace-role]', items => items.at(-1).textContent)
  check('newest message shows, with the callback token and MCP credential redacted',
    lastText.includes('[redacted]') && !lastText.includes(TOKEN) && !lastText.includes(MCP), lastText.slice(0, 120))
  await holder.screenshot({ path: `${OUT}/427-shared-recent.png` })

  let clicks = 0
  while (await holder.$('[data-load-earlier]') && clicks < 20) {
    const before = await holder.$$eval('[data-workspace-role]', items => items.length)
    await holder.click('[data-load-earlier]')
    await holder.waitForFunction(n => document.querySelectorAll('[data-workspace-role]').length > n, before, { timeout: 15_000 })
    clicks++
  }
  const firstText = await holder.$eval('[data-workspace-role]', item => item.textContent)
  check('"Load earlier messages" pages back to the first review of the day', firstText.includes(FIRST), `${clicks} click(s); first: ${firstText.slice(0, 80)}`)
  const body = await holder.textContent('body')
  check('nowhere on the page: callback token or MCP credential', !body.includes(TOKEN) && !body.includes(MCP))
  await holder.$eval('[data-workspace-role]', item => item.scrollIntoView())
  await holder.screenshot({ path: `${OUT}/427-shared-first.png` })

  const named = await (await api(bob, 'GET', '/api/sessions/reviews/conversation?offset=0')).json()
  check('named Viewer reads the same history from the start', named.entries?.[0]?.text === FIRST, `${named.entries?.length} entries, truncated=${named.truncated}`)

  // Revoke and expiry.
  const deleted = await api(alice, 'DELETE', `/api/ee/sessions/reviews/shares/links/${viewerLink.link.id}`)
  await holder.waitForSelector('.shared-state.error', { timeout: 15_000 })
  check('deleting the link closes the open shared view on its next poll', deleted.ok(), await holder.textContent('.shared-state'))
  await holder.screenshot({ path: `${OUT}/427-shared-revoked.png` })
  const afterDelete = await holder.request.fetch(`${UI}/api/shared/${tokenOf(viewerLink)}/conversation?offset=0`)
  check('deleted link: conversation answers 404', afterDelete.status() === 404, String(afterDelete.status()))

  const beforeExpiry = await holder.request.fetch(`${UI}/api/shared/${tokenOf(collabLink)}/conversation?offset=0`)
  check('Collaborator link reads the first review while valid', (await beforeExpiry.json()).entries?.[0]?.text === FIRST)
  await api(alice, 'POST', `/api/proof/expire/${collabLink.link.id}`)
  const expired = await holder.request.fetch(`${UI}/api/shared/${tokenOf(collabLink)}/conversation?offset=0`)
  const expiredText = await holder.request.fetch(`${UI}/api/shared/${tokenOf(collabLink)}/transcript`)
  check('expired link: conversation and transcript answer 404', expired.status() === 404 && expiredText.status() === 404,
    `${expired.status()} ${expiredText.status()}`)

  await api(alice, 'DELETE', '/api/ee/sessions/reviews/shares/users/bob')
  const unshared = await api(bob, 'GET', '/api/sessions/reviews/conversation?offset=0')
  check('unshared named Viewer: 404', unshared.status() === 404, String(unshared.status()))

  check('no page errors', errors.length === 0, errors.join(' | '))
  await browser.close()
  const failed = results.filter(r => !r.ok)
  console.log(`${results.length - failed.length}/${results.length} passed`)
  process.exit(failed.length ? 1 : 0)
})().catch(e => { console.error(e); process.exit(1) })
