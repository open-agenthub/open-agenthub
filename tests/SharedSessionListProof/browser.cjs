// Two users in one Chromium (#407/#410): alice shares, bob's sidebar and Sessions page follow
// without a reload. Runs against Program.cs + Vite; see README.md.
// Env: UI (default http://127.0.0.1:5173), PW_MODULE (playwright-core), CHROME (executable),
// OUT (screenshot directory), EXPECT=branch|main (main = the baseline before the change).
const { chromium } = require(process.env.PW_MODULE || 'playwright-core')
const UI = process.env.UI || 'http://127.0.0.1:5173'
const OUT = process.env.OUT || '.'
const EXPECT = process.env.EXPECT || 'branch'
const results = []
const check = (name, ok, detail = '') => { results.push({ name, ok: !!ok, detail }); console.log(`${ok ? 'PASS' : 'FAIL'} ${name}${detail ? ' — ' + detail : ''}`) }

async function signedIn(browser, user) {
  const context = await browser.newContext({ viewport: { width: 1440, height: 1000 } })
  await context.addCookies([{ name: 'proof-user', value: user, url: UI }])
  const page = await context.newPage()
  page.errors = []
  page.on('pageerror', error => page.errors.push(String(error)))
  await page.goto(UI + '/')
  await page.waitForSelector('[data-session-row]')
  return page
}
const rows = page => page.$$eval('.side-sessions [data-session-row]', items => items.map(item => ({
  id: item.dataset.sessionRow, childOf: item.dataset.childOf || null,
  nested: item.classList.contains('nested'), text: item.querySelector('.meta')?.textContent || ''
})))
const api = (page, method, path, data) => page.request.fetch(UI + path, { method, data })

;(async () => {
  const browser = await chromium.launch({ executablePath: process.env.CHROME, args: ['--no-sandbox', '--disable-dev-shm-usage'] })
  const alice = await signedIn(browser, 'alice')
  const bob = await signedIn(browser, 'bob')

  check('before: bob sees only his own session', JSON.stringify((await rows(bob)).map(r => r.id)) === '["bob-own"]')

  const shared = await api(alice, 'POST', '/api/ee/sessions/release/shares/users', { recipient: 'bob', role: 'Collaborator' })
  const viewer = await api(alice, 'POST', '/api/ee/sessions/review/shares/users', { recipient: 'bob', role: 'Viewer' })
  const link = await api(alice, 'POST', '/api/ee/sessions/link-only/shares/links', { role: 'Collaborator', expiresAt: null })
  check('alice shares release (Collaborator), review (Viewer), link-only (link)', shared.ok() && viewer.ok() && link.ok(),
    `${shared.status()} ${viewer.status()} ${link.status()}`)

  // No reload: the app's five-second poll has to bring the shares in.
  const started = Date.now()
  const want = EXPECT === 'main' ? '[data-session-row="release"]' : '[data-session-row="links"]'
  await bob.waitForSelector(`.side-sessions ${want}`, { timeout: 15000 })
  check('shares appear in bob\'s sidebar without a reload', true, `${Date.now() - started} ms`)
  await bob.waitForTimeout(500)
  const after = await rows(bob)
  console.log(JSON.stringify(after))
  const ids = after.map(r => r.id)
  check('direct shares listed', ids.includes('release') && ids.includes('review'))
  check('link-only session not listed', !ids.includes('link-only'))
  check('another owner\'s session under the shared one not listed', !ids.includes('planted'))
  if (EXPECT === 'main') {
    check('baseline: children of the shared session are missing', !ids.includes('changelog') && !ids.includes('links'))
  } else {
    check('children follow their parent, nested', JSON.stringify(ids.slice(ids.indexOf('release'), ids.indexOf('release') + 3)) === '["release","changelog","links"]')
    const child = after.find(r => r.id === 'changelog'), grandchild = after.find(r => r.id === 'links')
    check('child marked nested, child of its parent, with role and owner', child.nested && child.childOf === 'release'
      && child.text.includes('Collaborator') && child.text.includes('by alice') && child.text.includes('child of Release prep'), child.text)
    check('grandchild nested under the child', grandchild.nested && grandchild.childOf === 'changelog', grandchild.text)
    const detail = await (await api(bob, 'GET', '/api/sessions/links')).json()
    check('grandchild opens with the inherited role', detail.accessRole === 'Collaborator' && detail.inheritedFrom === 'release'
      && detail.canWrite === true && detail.canManage === false, JSON.stringify({ accessRole: detail.accessRole, inheritedFrom: detail.inheritedFrom }))
  }
  const reviewRow = after.find(r => r.id === 'review')
  check('viewer session marked Viewer and its open question shown', reviewRow?.text.includes('Viewer') && reviewRow?.text.includes('Question open'), reviewRow?.text)
  const badge = await bob.$('.sessions-head .wait-badge')
  check('viewer question not counted as waiting on bob', EXPECT === 'main' ? true : badge === null, badge ? await badge.textContent() : 'no badge')
  await bob.locator('.side-sessions').screenshot({ path: `${OUT}/${EXPECT}-bob-sidebar-shared.png` })

  await bob.click('.sessions-head')
  await bob.waitForSelector('.sessions-page .row')
  await bob.screenshot({ path: `${OUT}/${EXPECT}-bob-sessions-page.png` })
  const sub = await bob.textContent('.sessions-page .sub')
  check('Sessions page counts', true, sub.trim())

  if (EXPECT !== 'main') {
    await bob.click('.side-sessions [data-session-row="changelog"]')
    await bob.waitForTimeout(1500)
    const header = await bob.textContent('body')
    check('child opens for bob, marked shared by alice', header.includes('Write changelog') && header.includes('shared by alice'))
    await bob.screenshot({ path: `${OUT}/${EXPECT}-bob-child-open.png` })
  }

  const revoke = await api(alice, 'DELETE', '/api/ee/sessions/release/shares/users/bob')
  check('alice revokes the parent share', revoke.ok(), String(revoke.status()))
  await bob.waitForFunction(() => !document.querySelector('.side-sessions [data-session-row="release"]'), null, { timeout: 15000 })
  await bob.waitForTimeout(300)
  const revoked = (await rows(bob)).map(r => r.id)
  check('revoking the parent removes parent and children from bob\'s list', !revoked.some(id => ['release', 'changelog', 'links'].includes(id)) && revoked.includes('review'), JSON.stringify(revoked))
  const gone = await api(bob, 'GET', '/api/sessions/links')
  check('grandchild no longer opens for bob', gone.status() === 404, String(gone.status()))
  await bob.locator('.side-sessions').screenshot({ path: `${OUT}/${EXPECT}-bob-sidebar-revoked.png` })

  check('no page errors', alice.errors.length === 0 && bob.errors.length === 0, [...alice.errors, ...bob.errors].join(' | '))
  await browser.close()
  const failed = results.filter(r => !r.ok)
  console.log(`${results.length - failed.length}/${results.length} passed`)
  process.exit(failed.length ? 1 : 0)
})().catch(error => { console.error(error); process.exit(2) })
