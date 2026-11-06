// Recipient suggestions in both share dialogs (#470), two users in one Chromium, against the
// ProjectSharingProof host (real sharing controllers, recipient search and Postgres) + Vite.
// Session dialog by mouse: alice types bob's full username, clicks the suggestion, picks
// Collaborator, presses Add; bob's sidebar shows the session and it opens for him.
// Project dialog by keyboard: ArrowDown, Escape, ArrowDown, Enter selects carol, Enter shares;
// carol's sidebar shows the project's sessions.
// Env: UI, PW_MODULE, CHROME, OUT as in ../ProjectSharingProof; EXPECT=branch|main
// (main = the build before the fix, where the click is expected to change nothing).
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
  await page.waitForSelector('.side-sessions')
  return page
}
const ids = page => page.$$eval('.side-sessions [data-session-row]', items => items.map(item => item.dataset.sessionRow))
const api = (page, method, path, data) => page.request.fetch(UI + path, { method, data })
const waitRow = (page, id) => page.waitForSelector(`.side-sessions [data-session-row="${id}"]`, { timeout: 15000 })
// What the person sees: the input's text, whether the suggestion list is on screen, and which
// element the input reports as highlighted.
const state = (page, input, list) => page.evaluate(([inputSelector, listSelector]) => {
  const field = document.querySelector(inputSelector)
  const box = document.querySelector(listSelector)
  const visible = !!box && !!(box.offsetWidth || box.offsetHeight || box.getClientRects().length)
  return { value: field?.value, listVisible: visible, expanded: field?.getAttribute('aria-expanded'),
    active: field?.getAttribute('aria-activedescendant') || null, dialogOpen: !!document.querySelector('[data-project-share]') }
}, [input, list])

;(async () => {
  const browser = await chromium.launch({ executablePath: process.env.CHROME, args: ['--no-sandbox', '--disable-dev-shm-usage'] })
  const alice = await signedIn(browser, 'alice')
  const bob = await signedIn(browser, 'bob')
  const carol = await signedIn(browser, 'carol')
  check('before: bob does not see alice\'s private session', !(await ids(bob)).includes('private'), JSON.stringify(await ids(bob)))

  // --- Session share dialog, mouse ---
  await alice.goto(UI + '/s/private')
  await alice.getByRole('button', { name: '↗ Share', exact: true }).click()
  const input = '[aria-label="Recipient username or email"]'
  const list = '[aria-label="Matching recipients"]'
  await alice.fill(input, 'bob')
  const suggestion = alice.locator(`${list} button, ${list} [role=option]`).first()
  await suggestion.waitFor()
  check('the lookup suggests bob', (await suggestion.innerText()).includes('Username: bob'), await suggestion.innerText())
  const before = await state(alice, input, list)
  await alice.screenshot({ path: `${OUT}/${EXPECT}-1-session-suggestion.png` })
  await suggestion.click()
  await alice.waitForTimeout(800) // past the 250 ms lookup debounce, so a re-search would have shown
  const after = await state(alice, input, list)
  await alice.screenshot({ path: `${OUT}/${EXPECT}-2-session-after-click.png` })
  console.log('session dialog before/after click', JSON.stringify({ before, after }))

  if (EXPECT === 'main') {
    check('baseline: the click leaves the text unchanged', after.value === before.value, `${before.value} → ${after.value}`)
    check('baseline: the suggestion list stays open', after.listVisible, JSON.stringify(after))
    check('baseline: nothing marks a selection', !(await alice.$('[data-recipient-chosen]')))
    await browser.close()
    return finish()
  }

  const marked = await alice.getAttribute(input, 'data-recipient-selected')
  check('the click selects bob and the input shows him', after.value === 'bob' && marked === 'bob', `${after.value} / ${marked}`)
  check('the click closes the suggestion list', !after.listVisible && after.expanded === 'false', JSON.stringify(after))
  const chosen = await alice.locator('[data-recipient-chosen]').innerText()
  check('a status line names the selection', chosen.includes('Username: bob'), chosen)
  check('the click alone does not share', (await (await api(alice, 'GET', '/api/ee/sessions/private/shares')).json()).users?.length === 0)
  await alice.selectOption('[aria-label="Role"]', 'Collaborator')
  await alice.getByRole('button', { name: 'Add', exact: true }).click()
  await alice.locator('.person .pname').filter({ hasText: 'bob' }).waitFor()
  const shares = await (await api(alice, 'GET', '/api/ee/sessions/private/shares')).json()
  check('Add shares with the chosen role', JSON.stringify((shares.users || []).map(u => [u.recipient, u.role])) === '[["bob","Collaborator"]]',
    JSON.stringify(shares.users))
  check('the input is empty again after Add', (await alice.inputValue(input)) === '')
  await alice.screenshot({ path: `${OUT}/branch-3-session-shared.png` })

  const t0 = Date.now()
  await waitRow(bob, 'private')
  check('bob\'s sidebar shows the shared session without a reload', true, `${Date.now() - t0} ms`)
  await bob.click('.side-sessions [data-session-row="private"]')
  await bob.waitForURL(/\/s\/private/)
  const detail = await (await api(bob, 'GET', '/api/sessions/private')).json()
  check('the session opens for bob as Collaborator', detail.accessRole === 'Collaborator', detail.accessRole)
  await bob.screenshot({ path: `${OUT}/branch-4-bob-sees-session.png` })

  // --- Project share dialog, keyboard only ---
  const release = (await (await api(alice, 'GET', '/api/proof/release')).json()).id
  await alice.goto(UI + '/')
  await alice.hover(`[data-project-drop="${release}"]`)
  await alice.click(`[data-project-share-open="${release}"]`)
  await alice.waitForSelector('[data-project-share-empty]')
  const subject = '[data-project-share-subject]'
  const users = '[aria-label="Matching users"]'
  await alice.focus(subject)
  await alice.keyboard.type('car')
  await alice.locator(`${users} [role=option]`).first().waitFor()
  await alice.keyboard.press('ArrowDown')
  let s = await state(alice, subject, users)
  const firstId = await alice.locator(`${users} [role=option]`).first().getAttribute('id')
  check('ArrowDown highlights the first suggestion', s.active === firstId, JSON.stringify(s))
  await alice.screenshot({ path: `${OUT}/branch-5-project-highlight.png` })
  await alice.keyboard.press('Escape')
  s = await state(alice, subject, users)
  check('Escape closes the list and keeps the dialog open', !s.listVisible && s.dialogOpen, JSON.stringify(s))
  await alice.keyboard.press('ArrowDown')
  await alice.keyboard.press('Enter')
  s = await state(alice, subject, users)
  const carolMarked = await alice.getAttribute(subject, 'data-recipient-selected')
  check('Enter selects carol and closes the list', s.value === 'carol' && carolMarked === 'carol' && !s.listVisible, JSON.stringify(s))
  await alice.screenshot({ path: `${OUT}/branch-6-project-selected.png` })
  await alice.keyboard.press('Enter')
  await alice.waitForSelector('[data-project-share-entry="user:carol"]')
  check('a second Enter shares the project with carol', true)

  await waitRow(carol, 'qa')
  const carolIds = await ids(carol)
  check('carol\'s sidebar shows the project\'s sessions', ['prep', 'qa'].every(id => carolIds.includes(id)), JSON.stringify(carolIds))

  check('no page errors', [alice, bob, carol].every(page => !page.errors.length), JSON.stringify([alice, bob, carol].map(page => page.errors)))
  await browser.close()
  finish()
})().catch(error => { console.error(error); process.exit(1) })

function finish() {
  const failed = results.filter(result => !result.ok)
  console.log(`${results.length - failed.length}/${results.length} passed`)
  process.exit(failed.length ? 1 : 0)
}
