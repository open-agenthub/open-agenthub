// Two users in one Chromium (#409): alice shares her project from the project controls, bob's and
// carol's sidebars follow without a reload, a session created later appears, a session share only
// adds, removing the share and deleting the project close everything. Runs against Program.cs +
// Vite; see README.md.
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
  page.on('dialog', dialog => dialog.accept())
  await page.goto(UI + '/')
  await page.waitForSelector('.side-sessions')
  return page
}
const rows = page => page.$$eval('.side-sessions [data-session-row]', items => items.map(item => ({
  id: item.dataset.sessionRow, childOf: item.dataset.childOf || null,
  nested: item.classList.contains('nested'), text: item.querySelector('.meta')?.textContent || ''
})))
const ids = async page => (await rows(page)).map(row => row.id)
const api = (page, method, path, data) => page.request.fetch(UI + path, { method, data })
const waitRow = (page, id, present = true) => page.waitForFunction(([selector, want]) =>
  !!document.querySelector(selector) === want, [`.side-sessions [data-session-row="${id}"]`, present], { timeout: 15000 })

;(async () => {
  const browser = await chromium.launch({ executablePath: process.env.CHROME, args: ['--no-sandbox', '--disable-dev-shm-usage'] })
  const alice = await signedIn(browser, 'alice')
  const bob = await signedIn(browser, 'bob')
  const carol = await signedIn(browser, 'carol')
  const dave = await signedIn(browser, 'dave')
  const release = (await (await api(alice, 'GET', '/api/proof/release')).json()).id

  check('before: bob sees only his own session', JSON.stringify(await ids(bob)) === '["bob-own"]', JSON.stringify(await ids(bob)))

  // alice opens the project's share dialog from the sidebar and adds bob and her group.
  await alice.hover(`[data-project-drop="${release}"]`)
  const shareButton = await alice.$(`[data-project-share-open="${release}"]`)
  if (EXPECT === 'main') {
    check('baseline: the project controls have no share action', shareButton === null)
    const put = await api(alice, 'PUT', `/api/ee/projects/${release}/shares`, { subjectType: 'user', subject: 'bob', role: 'Viewer' })
    check('baseline: no project share endpoint', put.status() === 404 || put.status() === 405, String(put.status()))
    await bob.waitForTimeout(6000)
    check('baseline: bob still sees none of alice\'s project', JSON.stringify(await ids(bob)) === '["bob-own"]')
    return finish(browser, [alice, bob, carol, dave])
  }
  await shareButton.click()
  await alice.waitForSelector('[data-project-share-empty]')
  check('dialog opens and says only alice has access', true)
  await alice.fill('[data-project-share-subject]', 'bob')
  await alice.waitForSelector('[aria-label="Matching users"] button')
  await alice.click('[aria-label="Matching users"] button')
  await alice.click('[data-project-share-add]')
  await alice.waitForSelector('[data-project-share-entry="user:bob"]')
  await alice.selectOption('[data-project-share-kind]', 'group')
  await alice.waitForSelector('[aria-label="Your groups"] button')
  await alice.click('[aria-label="Your groups"] button')
  await alice.selectOption('[data-project-share-role]', 'Collaborator')
  await alice.click('[data-project-share-add]')
  await alice.waitForSelector('[data-project-share-entry="group:release-team"]')
  await alice.locator('[data-project-share] .modal').screenshot({ path: `${OUT}/alice-project-share-dialog.png` })
  const listed = await (await api(alice, 'GET', `/api/ee/projects/${release}/shares`)).json()
  check('dialog lists bob (Viewer) and release-team (Collaborator)', JSON.stringify(listed.map(s => [s.subjectType, s.subject, s.role]))
    === JSON.stringify([['user', 'bob', 'Viewer'], ['group', 'release-team', 'Collaborator']]), JSON.stringify(listed.map(s => [s.subjectType, s.subject, s.role])))
  const foreignGroup = await api(alice, 'PUT', `/api/ee/projects/${release}/shares`, { subjectType: 'group', subject: 'other-team', role: 'Viewer' })
  check('a group alice is not in is refused', foreignGroup.status() === 400, String(foreignGroup.status()))
  await alice.click('[data-project-share] .row button')

  // No reload: the five-second poll brings the project's sessions in.
  const started = Date.now()
  await waitRow(bob, 'qa')
  check('project sessions appear in bob\'s sidebar without a reload', true, `${Date.now() - started} ms`)
  await bob.waitForTimeout(400)
  const bobRows = await rows(bob)
  console.log(JSON.stringify(bobRows))
  const bobIds = bobRows.map(row => row.id)
  check('bob sees prep, its child notes and qa — not private, not carol\'s planted one',
    ['prep', 'notes', 'qa'].every(id => bobIds.includes(id)) && !bobIds.includes('private') && !bobIds.includes('planted'), JSON.stringify(bobIds))
  const qa = bobRows.find(row => row.id === 'qa'), notes = bobRows.find(row => row.id === 'notes')
  check('rows say Viewer, by alice, via project Release', qa.text.includes('Viewer') && qa.text.includes('by alice') && qa.text.includes('via project Release'), qa.text)
  check('the child outside the project is nested under its parent', notes.nested && notes.childOf === 'prep', notes.text)
  const notesDetail = await (await api(bob, 'GET', '/api/sessions/notes')).json()
  check('child opens for bob, inherited from prep via the project', notesDetail.accessRole === 'Viewer' && notesDetail.inheritedFrom === 'prep'
    && notesDetail.sharedViaProject === 'Release' && notesDetail.canWrite === false, JSON.stringify({ r: notesDetail.accessRole, i: notesDetail.inheritedFrom, p: notesDetail.sharedViaProject }))
  await bob.locator('.side-sessions').screenshot({ path: `${OUT}/bob-sidebar-project-shared.png` })

  await waitRow(carol, 'qa')
  const carolQa = (await rows(carol)).find(row => row.id === 'qa')
  check('carol, through release-team, sees the sessions as Collaborator', carolQa.text.includes('Collaborator'), carolQa.text)
  await dave.waitForTimeout(1000)
  check('dave, not in the group, sees nothing of the project', !(await ids(dave)).some(id => ['prep', 'qa', 'notes'].includes(id)))

  // A session created in the project afterwards is shared with no further step.
  const created = await api(alice, 'POST', '/api/proof/sessions', { id: 'hotfix', title: 'Hotfix after share', projectId: release })
  check('alice creates a new session in the project', created.ok(), String(created.status()))
  const t0 = Date.now()
  await waitRow(bob, 'hotfix')
  check('the new session appears for bob without a reload', true, `${Date.now() - t0} ms`)

  // Precedence: a session share adds to the project's role, never takes from it.
  await api(alice, 'POST', '/api/ee/sessions/qa/shares/users', { recipient: 'bob', role: 'Collaborator' })
  await api(alice, 'POST', '/api/ee/sessions/qa/shares/users', { recipient: 'carol', role: 'Viewer' })
  const bobQa = await (await api(bob, 'GET', '/api/sessions/qa')).json()
  const carolQaDetail = await (await api(carol, 'GET', '/api/sessions/qa')).json()
  check('session share Collaborator lifts bob above the project\'s Viewer', bobQa.accessRole === 'Collaborator' && bobQa.canWrite === true, bobQa.accessRole)
  check('session share Viewer does not lower carol\'s project Collaborator', carolQaDetail.accessRole === 'Collaborator', carolQaDetail.accessRole)

  // The session's own dialog shows the project's shares read-only.
  const overview = await (await api(alice, 'GET', '/api/ee/sessions/qa/shares')).json()
  check('session overview carries the project shares', (overview.projectShares || []).length === 2, JSON.stringify(overview.projectShares?.map(s => s.subject)))

  // alice removes bob in the dialog: everything but the session shared with him directly closes.
  await alice.hover(`[data-project-drop="${release}"]`)
  await alice.click(`[data-project-share-open="${release}"]`)
  await alice.waitForSelector('[data-project-share-entry-role="user:bob"]')
  await alice.selectOption('[data-project-share-entry-role="user:bob"]', 'Remove')
  await alice.waitForFunction(() => !document.querySelector('[data-project-share-entry="user:bob"]'))
  await alice.click('[data-project-share] .row button')
  await waitRow(bob, 'prep', false)
  await bob.waitForTimeout(300)
  const afterRevoke = await ids(bob)
  check('revoking the project share closes its sessions for bob, keeps his explicit share on qa',
    !['prep', 'notes', 'hotfix'].some(id => afterRevoke.includes(id)) && afterRevoke.includes('qa'), JSON.stringify(afterRevoke))
  const gone = await api(bob, 'GET', '/api/sessions/notes')
  check('the child no longer opens for bob', gone.status() === 404, String(gone.status()))
  const qaNow = await (await api(bob, 'GET', '/api/sessions/qa')).json()
  check('qa now opens for bob through the session share only', qaNow.accessRole === 'Collaborator' && !qaNow.sharedViaProject, JSON.stringify({ r: qaNow.accessRole, p: qaNow.sharedViaProject }))
  await bob.locator('.side-sessions').screenshot({ path: `${OUT}/bob-sidebar-project-revoked.png` })

  // Deleting the project removes its shares: carol keeps only her explicit share on qa.
  await alice.hover(`[data-project-drop="${release}"]`)
  await alice.click(`[data-project-drop="${release}"] button[title="Delete project"]`)
  await waitRow(carol, 'prep', false)
  await carol.waitForTimeout(300)
  const carolAfter = await ids(carol)
  check('deleting the project closes it for the group', !['prep', 'notes', 'hotfix'].some(id => carolAfter.includes(id)), JSON.stringify(carolAfter))
  const carolQaAfter = await (await api(carol, 'GET', '/api/sessions/qa')).json()
  check('carol\'s explicit Viewer share on qa remains, now as Viewer', carolQaAfter.accessRole === 'Viewer', carolQaAfter.accessRole)
  const stale = await api(alice, 'GET', `/api/ee/projects/${release}/shares`)
  check('the deleted project has no shares left to list', stale.status() === 404, String(stale.status()))

  return finish(browser, [alice, bob, carol, dave])
})().catch(error => { console.error(error); process.exit(2) })

async function finish(browser, pages) {
  const errors = pages.flatMap(page => page.errors)
  check('no page errors', errors.length === 0, errors.join(' | '))
  await browser.close()
  const failed = results.filter(r => !r.ok)
  console.log(`${results.length - failed.length}/${results.length} passed`)
  process.exit(failed.length ? 1 : 0)
}
