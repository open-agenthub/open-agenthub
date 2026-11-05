// Real sharing APIs feed two isolated signed-in browser contexts; no session list is mocked.
const assert = require('node:assert/strict')
const { chromium } = require(process.env.PW_MODULE || 'playwright-core')
const UI = process.env.UI || 'http://localhost:5173'
const baseline = process.env.EXPECT === 'main'
const output = process.env.OUT || '/tmp'
;(async () => {
  const browser = await chromium.launch({ executablePath: process.env.CHROME, args: ['--no-sandbox', '--disable-dev-shm-usage'] })
  try {
    async function user(name) {
      const ctx = await browser.newContext({ viewport: { width: 1440, height: 1100 } })
      await ctx.addCookies([{ name: 'proof-user', value: name, url: UI }])
      const page = await ctx.newPage()
      await page.goto(UI)
      await page.waitForSelector('.home')
      return page
    }
    const alice = await user('alice'), bob = await user('bob')
    const request = async (method, path, data) => {
      const response = await alice.request.fetch(UI + path, { method, data })
      assert(response.ok(), `${method} ${path}: ${response.status()} ${await response.text()}`)
      return response
    }
    const project = (await (await request('GET', '/api/proof/release')).json()).id
    await request('PUT', `/api/ee/projects/${project}/shares`, { subjectType: 'user', subject: 'bob', role: 'Viewer' })
    await request('POST', '/api/ee/sessions/private/shares/users', { recipient: 'bob', role: 'Collaborator' })
    await bob.waitForSelector('.side-sessions [data-session-row="notes"]', { timeout: 15000 })
    if (baseline) {
      assert.equal(await bob.locator('[data-home-shared]').count(), 0)
      console.log('PASS baseline: shared list exists in sidebar but home has no shared section')
      return
    }
    await bob.waitForSelector('[data-home-session="notes"]')
    const text = await bob.locator('[data-home-shared]').innerText()
    for (const title of ['Release prep', 'Release notes', 'Release QA', 'Private notes']) assert(text.includes(title), title)
    assert(text.includes('Viewer · shared by alice'))
    assert(text.includes('Collaborator · shared by alice'))
    assert((await bob.locator('[data-home-session="qa"]').innerText()).includes('Question open'))
    assert.equal(await bob.locator('.ny-card').count(), 1)
    assert((await bob.locator('.ny-card').innerText()).includes('Private notes'))
    assert.equal(await bob.locator('.headline').innerText(), 'One agent wants a word with you.')
    assert(!(await alice.locator('[data-home-shared]').count()))
    await bob.screenshot({ path: `${output}/home-shared-bob.png`, fullPage: true })
    await bob.locator('[data-home-session="notes"]').click()
    await bob.waitForURL('**/s/notes')
    assert((await bob.locator('body').innerText()).includes('shared by alice'))
    await bob.goto(UI)
    await request('DELETE', `/api/ee/projects/${project}/shares/user/bob`)
    await bob.waitForFunction(() => !document.querySelector('[data-home-session="qa"]'), null, { timeout: 15000 })
    assert.equal(await bob.locator('[data-home-session="notes"]').count(), 0)
    assert.equal(await bob.locator('[data-home-session="private"]').count(), 1)
    console.log('PASS home: direct + inherited + project, role/owner/status, Viewer excluded from waiting, navigation and live revocation')
  } finally { await browser.close() }
})().catch(e => { console.error(e); process.exitCode = 1 })
