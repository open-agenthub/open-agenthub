// Link role vs. person role in the session share dialog (#519), against the ProjectSharingProof
// host (real sharing controllers, shared-link endpoints and Postgres) + Vite.
// alice adds bob as Collaborator, then creates a link without touching any link control.
// branch: the link is Viewer, its role is shown before and after creating, and dave, opening it,
// is read-only. A Collaborator link alice picks on purpose is the control: dave can type there.
// main: the same clicks yield a Collaborator link (the bug).
// Env: UI, PW_MODULE, CHROME, OUT as in ../ProjectSharingProof; EXPECT=branch|main.
const { chromium } = require(process.env.PW_MODULE || 'playwright-core')
const UI = process.env.UI || 'http://127.0.0.1:5173'
const OUT = process.env.OUT || '.'
const EXPECT = process.env.EXPECT || 'branch'
const results = []
const check = (name, ok, detail = '') => { results.push({ name, ok: !!ok, detail }); console.log(`${ok ? 'PASS' : 'FAIL'} ${name}${detail ? ' — ' + detail : ''}`) }
const api = (page, method, path, data) => page.request.fetch(UI + path, { method, data })

async function signedIn(browser, user) {
  const context = await browser.newContext({ viewport: { width: 1440, height: 1000 } })
  await context.addCookies([{ name: 'proof-user', value: user, url: UI }])
  const page = await context.newPage()
  await page.goto(UI + '/')
  await page.waitForSelector('.side-sessions')
  return page
}
// main has no data-create-link; its only "Create link" button is the one to press there.
const createButton = page => EXPECT === 'main' ? page.getByRole('button', { name: 'Create link', exact: true }) : page.locator('[data-create-link]')
const shares = async page => (await api(page, 'GET', '/api/ee/sessions/private/shares')).json()

async function openShare(page) {
  await page.goto(UI + '/s/private')
  await page.getByRole('button', { name: '↗ Share', exact: true }).click()
  await page.waitForSelector('[aria-label="Recipient username or email"]')
}
async function createLink(page) {
  await createButton(page).click()
  await page.locator('.one-time code').waitFor()
  return page.locator('.one-time code').innerText()
}
// What a link holder gets: the backend's role for the token, and whether the page marks itself
// read-only (TerminalView shows "Read-only" exactly when the session's capabilities deny writing).
async function visit(page, url) {
  const token = new URL(url).pathname.split('/').pop()
  const info = await (await api(page, 'GET', `/api/shared/${encodeURIComponent(token)}/session`)).json()
  // The host signs links with its configured public origin (example.com); the path is what counts.
  await page.goto(UI + new URL(url).pathname)
  await page.locator('.shared-meta').waitFor()
  await page.waitForTimeout(500)
  return { accessRole: info.accessRole, canWrite: info.canWrite, header: await page.locator('.shared-meta').innerText(),
    readOnlyBadge: await page.locator('.readonly', { hasText: 'Read-only' }).isVisible() }
}

;(async () => {
  const browser = await chromium.launch({ executablePath: process.env.CHROME, args: ['--no-sandbox', '--disable-dev-shm-usage'] })
  const alice = await signedIn(browser, 'alice')
  const dave = await signedIn(browser, 'dave')
  check('before: nothing is shared', (await shares(alice)).users.length === 0 && (await shares(alice)).links.length === 0)

  await openShare(alice)
  await alice.fill('[aria-label="Recipient username or email"]', 'bob')
  await alice.locator('[aria-label="Matching recipients"] [role=option]').first().click()
  await alice.selectOption(EXPECT === 'main' ? '[aria-label="Role"]' : '[aria-label="Role for this person"]', 'Collaborator')
  await alice.getByRole('button', { name: 'Add', exact: true }).click()
  await alice.locator('.person .pname').filter({ hasText: 'bob' }).waitFor()
  const users = (await shares(alice)).users.map(u => [u.recipient, u.role])
  check('bob is added as Collaborator', JSON.stringify(users) === '[["bob","Collaborator"]]', JSON.stringify(users))

  if (EXPECT !== 'main') {
    check('the link role still reads Viewer', (await alice.inputValue('[data-new-link-role]')) === 'Viewer')
    const label = await createButton(alice).innerText()
    check('the button names the role before creating', label === 'Create Viewer link', label)
    check('the note says holders cannot type', (await alice.locator('[data-new-link-role-note]').innerText()).includes('not type'))
  }
  await alice.screenshot({ path: `${OUT}/${EXPECT}-1-before-link.png` })
  const url = await createLink(alice)
  const links = (await shares(alice)).links.map(l => l.role)
  await alice.screenshot({ path: `${OUT}/${EXPECT}-2-link-created.png` })

  const visitor = await visit(dave, url)
  await dave.screenshot({ path: `${OUT}/${EXPECT}-3-visitor.png` })
  console.log('link roles', JSON.stringify(links), 'visitor', JSON.stringify(visitor))

  if (EXPECT === 'main') {
    check('baseline: the untouched link is created as Collaborator', JSON.stringify(links) === '["Collaborator"]', JSON.stringify(links))
    check('baseline: the link visitor can type', visitor.accessRole === 'Collaborator' && !visitor.readOnlyBadge, JSON.stringify(visitor))
    await browser.close()
    return finish()
  }

  check('the untouched link is created as Viewer', JSON.stringify(links) === '["Viewer"]', JSON.stringify(links))
  check('the one-time banner names the role', (await alice.locator('[data-one-time-link-role]').innerText()) === 'Viewer')
  const linkId = (await shares(alice)).links[0].id
  check('the link list shows the role', (await alice.locator(`[data-link-role-badge="${linkId}"]`).innerText()) === 'Viewer · read only')
  check('the link visitor is a Viewer and cannot type',
    visitor.accessRole === 'Viewer' && visitor.canWrite === false && visitor.readOnlyBadge && visitor.header.startsWith('Viewer'), JSON.stringify(visitor))

  // Closing and reopening starts at Viewer again, even after Collaborator was picked.
  await alice.selectOption('[data-new-link-role]', 'Collaborator')
  await alice.getByRole('button', { name: '↗ Share', exact: true }).click()
  await alice.getByRole('button', { name: '↗ Share', exact: true }).click()
  await alice.waitForSelector('[data-new-link-role]')
  check('reopening the dialog resets the link role to Viewer', (await alice.inputValue('[data-new-link-role]')) === 'Viewer')

  // Control: a Collaborator link chosen on purpose is labelled as such and lets its holder type,
  // so the read-only check above can tell the two apart.
  await alice.selectOption('[data-new-link-role]', 'Collaborator')
  const collabLabel = await createButton(alice).innerText()
  check('choosing Collaborator is visible on the button', collabLabel === 'Create Collaborator link', collabLabel)
  const collabUrl = await createLink(alice)
  check('the banner names a Collaborator link', (await alice.locator('[data-one-time-link-role]').innerText()) === 'Collaborator')
  check('the link role falls back to Viewer after creating', (await alice.inputValue('[data-new-link-role]')) === 'Viewer')
  await alice.screenshot({ path: `${OUT}/${EXPECT}-4-two-links.png` })
  const control = await visit(dave, collabUrl)
  check('control: the Collaborator link visitor can type', control.accessRole === 'Collaborator' && control.canWrite !== false && !control.readOnlyBadge,
    JSON.stringify(control))
  await browser.close()
  finish()
})().catch(error => { console.error(error); process.exit(2) })

function finish() {
  require('fs').writeFileSync(`${OUT}/${EXPECT}-results.json`, JSON.stringify(results, null, 2))
  const failed = results.filter(r => !r.ok).length
  console.log(`${results.length - failed}/${results.length} passed`)
  process.exit(failed ? 1 : 0)
}
