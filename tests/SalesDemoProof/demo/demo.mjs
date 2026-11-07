// The sales demo (#481), scripted end to end in real Chromium against the demo instance, and
// recorded while it runs. Needs: chromes.sh (alice, bob, carol signed in), reset-demo.sh run
// just before, the stand-ins from standins.yaml. Usage:
//   node demo.mjs <outdir>
// Writes <outdir>/frames/*.jpg (the screencast of whichever user is on screen) and
// <outdir>/timeline.json (frame times, scene starts, waits that the edit may compress, and
// facts measured on the way). Nothing in the app is mocked; the stand-ins are the model and
// the Git host only, and the scenes say so on screen in the edit.
import fs from 'node:fs'
import path from 'node:path'
import { attach, ensureSignedIn, HUB } from './cdp.mjs'

const out = path.resolve(process.argv[2] || 'demo-out')
fs.mkdirSync(`${out}/frames`, { recursive: true })
const now = () => Date.now() / 1000
const timeline = { started: new Date().toISOString(), frames: [], marks: [], facts: {} }
const save = () => fs.writeFileSync(`${out}/timeline.json`, JSON.stringify(timeline, null, 1))

// --- recording ---------------------------------------------------------------------------
// CDP screencast sends a frame only when the page repaints; the edit holds each frame until
// the next one. Switching users stops one screencast and starts the other, seeded with a
// screenshot so the cut shows the new page at once.
let cdp = null
let frameNo = 0
function addFrame(buffer) {
  const file = `frames/${String(frameNo++).padStart(6, '0')}.jpg`
  fs.writeFileSync(`${out}/${file}`, buffer)
  timeline.frames.push({ t: now(), file, user: onScreen })
}
let onScreen = null
async function film(user, page) {
  if (cdp) { await cdp.send('Page.stopScreencast').catch(() => {}); await cdp.detach().catch(() => {}) }
  onScreen = user
  addFrame(await page.screenshot({ type: 'jpeg', quality: 85 }))
  cdp = await page.context().newCDPSession(page)
  cdp.on('Page.screencastFrame', f => {
    addFrame(Buffer.from(f.data, 'base64'))
    cdp.send('Page.screencastFrameAck', { sessionId: f.sessionId }).catch(() => {})
  })
  await cdp.send('Page.startScreencast', { format: 'jpeg', quality: 85, maxWidth: 1920, maxHeight: 1080, everyNthFrame: 1 })
}
const mark = (type, name) => { timeline.marks.push({ t: now(), type, name }); save() }
const scene = name => { console.log(new Date().toISOString(), 'scene', name); mark('scene', name) }
// A wait the edit may play faster; the edit labels it "sped up".
async function waiting(name, fn) { mark('wait-start', name); try { return await fn() } finally { mark('wait-end', name) } }

// --- showing actions -----------------------------------------------------------------------
// A ring around the element about to be used, drawn by this script (not part of the app), so a
// viewer can follow clicks in a recording that has no mouse pointer.
async function ring(page, locator, ms = 700) {
  await locator.scrollIntoViewIfNeeded().catch(() => {})
  const box = await locator.boundingBox()
  if (!box) return
  await page.evaluate(({ x, y, width, height }) => {
    const d = document.createElement('div')
    d.id = 'demo-ring'
    Object.assign(d.style, { position: 'fixed', left: `${x - 6}px`, top: `${y - 6}px`, width: `${width + 12}px`, height: `${height + 12}px`,
      border: '3px solid #f5b400', borderRadius: '10px', boxShadow: '0 0 0 4px rgba(245,180,0,.25)', zIndex: 2147483647, pointerEvents: 'none' })
    document.getElementById('demo-ring')?.remove()
    document.body.appendChild(d)
  }, box)
  await page.waitForTimeout(ms)
}
const unring = page => page.evaluate(() => document.getElementById('demo-ring')?.remove()).catch(() => {})
async function click(page, locator, ms) { await ring(page, locator, ms); await locator.click(); await page.waitForTimeout(250); await unring(page) }
async function type(page, locator, text, delay = 22) { await ring(page, locator, 400); await locator.click(); await locator.pressSequentially(text, { delay }); await unring(page) }
const pause = (page, ms) => page.waitForTimeout(ms)

// --- the demo ----------------------------------------------------------------------------------
const A = await attach('alice'), B = await attach('bob'), C = await attach('carol')
const alice = A.page, bob = B.page, carol = C.page
// bob and carol stay signed in but park on a blank page until their scenes: three live hub
// tabs polling the emulated VM slowed it enough that the first approval timed out.
for (const [u, p] of [['bob', bob], ['carol', carol]]) { await ensureSignedIn(p, u); await p.goto('about:blank') }
// alice starts signed out, so the recording shows the single sign-on.
await ensureSignedIn(alice, 'alice')
// Clicking "Sign out" alone left the identity provider's session in place, which signed alice
// straight back in; clearing both origins gives the first-visit sign-in a prospect sees.
const reset = await alice.context().newCDPSession(alice)
await reset.send('Network.clearBrowserCookies')
for (const origin of [HUB, 'https://id.example.com']) await reset.send('Storage.clearDataForOrigin', { origin, storageTypes: 'all' })
await reset.detach()
await alice.goto(HUB); await alice.waitForTimeout(1500)

// S1 — single sign-on through the team's identity provider
await film('alice', alice)
scene('signin')
await pause(alice, 1500)
await click(alice, alice.getByRole('button', { name: 'Sign in' }))
await alice.waitForSelector('#username', { timeout: 120000 })
await pause(alice, 800)
await type(alice, alice.locator('#username'), 'alice')
await type(alice, alice.locator('#password'), 'alice-demo-pw', 15)
await ring(alice, alice.locator('#kc-login'), 500)
await Promise.all([alice.waitForURL(u => u.toString().startsWith(HUB), { timeout: 120000 }), alice.locator('#kc-login').click()])
await alice.waitForLoadState('networkidle').catch(() => {})
await pause(alice, 3000)

// S2 — admin: licence, groups → roles, group budget, allowed agents
scene('admin')
await click(alice, alice.getByText('⚙', { exact: true }).first())
await pause(alice, 1200)
await click(alice, alice.getByText('License', { exact: true }))
await pause(alice, 3500)
await click(alice, alice.getByText('Usage limits & groups', { exact: true }))
await pause(alice, 2000)
const groupName = alice.getByPlaceholder('group name (as the IdP sends it)')
for (const [g, role] of [['platform', 'admin'], ['developers', 'user'], ['reviewers', 'user']]) {
  await type(alice, groupName, g)
  const roleSelect = groupName.locator('xpath=following::select[1]')
  await ring(alice, roleSelect, 300); await roleSelect.selectOption(role); await unring(alice)
  await click(alice, alice.getByRole('button', { name: 'Map', exact: true }), 400)
  await pause(alice, 900)
}
const scopeSelect = alice.locator('[data-limit-target]').locator('xpath=preceding::select[1]')
await ring(alice, scopeSelect, 400); await scopeSelect.selectOption('group'); await unring(alice)
await type(alice, alice.locator('[data-limit-target]'), 'developers')
await type(alice, alice.locator('[data-limit-amount]'), '50')
await click(alice, alice.locator('[data-add-limit]'))
await pause(alice, 1200)
for (const agent of ['Claude', 'Codex', 'OpenCode']) await click(alice, alice.locator(`[data-allowed-agent="${agent}"] input`), 350)
await click(alice, alice.getByRole('button', { name: 'Save agents' }))
await pause(alice, 2500)

// S3 — a project for the invoice service, shared read-only with carol
scene('project')
alice.once('dialog', d => d.accept('Invoice service'))
await click(alice, alice.getByRole('button', { name: '+ New project' }))
await pause(alice, 2000)
const projectRow = alice.locator('[data-project-share-open]').first()
await alice.getByText('Invoice service').first().hover()
await click(alice, projectRow)
await pause(alice, 1200)
await type(alice, alice.locator('[data-project-share-role]').locator('xpath=preceding::input[1]'), 'carol')
await pause(alice, 1200)
const carolOption = alice.locator('[data-recipient-option]').filter({ hasText: /carol/i }).first()
if (await carolOption.isVisible().catch(() => false)) await click(alice, carolOption, 300)
await click(alice, alice.locator('[data-project-share-add]'))
await pause(alice, 2000)
await click(alice, alice.getByRole('button', { name: 'Close', exact: true }).last())
await pause(alice, 800)

// S4 — issue #42 → agent session → approval → pushed branch
scene('session')
await click(alice, alice.getByRole('button', { name: '+ Give an agent a task' }).first())
await pause(alice, 1000)
await type(alice, alice.getByPlaceholder('e.g. Resolve ticket OPS-1423'), 'Issue #42: invoice totals one cent short')
await click(alice, alice.getByText('Own endpoint', { exact: true }), 400)
await type(alice, alice.getByPlaceholder('https://llm.example.com/v1'), 'http://model.demo.svc.cluster.local:443', 8)
await type(alice, alice.getByPlaceholder('model id, e.g. qwen3-coder-30b'), 'standin-fast')
await type(alice, alice.getByPlaceholder('Describe the task in plain language'), 'DEMO-ISSUE Fix issue #42 from ISSUE-42.md: invoice totals are one cent short. Work on a branch, run the tests, push.', 10)
const projectSelect = alice.locator('select').filter({ hasText: 'No project' }).first()
await ring(alice, projectSelect, 400); await projectSelect.selectOption({ label: 'Invoice service' }); await unring(alice)
await type(alice, alice.getByPlaceholder('…or paste a clone URL (git@… or https://…)'), 'git://git.demo.svc.cluster.local:443/invoice-service.git', 8)
await click(alice, alice.getByRole('button', { name: 'Add', exact: true }).last(), 400)
const started = now()
await click(alice, alice.getByRole('button', { name: 'Start session' }))
const allow = alice.getByRole('button', { name: "Allow (don't ask again)" })
await waiting('agent-start', async () => {
  // On the emulated VM the Workspace sometimes kept its first, failed connection ("disconnected")
  // while the pod came up; a reload, as a user would do, reconnects it. Counted in the facts.
  timeline.facts.workspaceReloads = 0
  for (let i = 0; i < 6; i++) {
    if (await allow.waitFor({ timeout: 90000 }).then(() => true, () => false)) return
    if (await alice.getByText('Do you want to proceed?').isVisible().catch(() => false))
      throw Error('the CLI asked in its own menu: the hub approval did not answer in time (VM overloaded); take is not usable')
    timeline.facts.workspaceReloads++
    await alice.reload(); await alice.waitForTimeout(3000)
  }
  throw Error('no approval within 9 minutes')
})
timeline.facts.startToApprovalSeconds = Math.round(now() - started)
await pause(alice, 2500)
await click(alice, allow, 1000)
await waiting('agent-push', () => alice.getByText('is pushed and ready for review').first().waitFor({ timeout: 300000 }))
timeline.facts.startToPushSeconds = Math.round(now() - started)
await pause(alice, 2500)
const result = alice.getByText('Result', { exact: true }).last()
await click(alice, result, 400)
await pause(alice, 3500)
await click(alice, result, 300)

// S5 — share: bob as Collaborator and a Viewer link. The dialog's MCP section is left out of
// this demo until the restriction is shown denying a call (#514).
scene('share')
await click(alice, alice.getByRole('button', { name: /Share/ }).first())
await pause(alice, 1200)
await type(alice, alice.getByPlaceholder('Name, username, or exact email…'), 'bob')
await pause(alice, 1200)
const bobOption = alice.locator('[data-recipient-option]').filter({ hasText: /bob/i }).first()
if (await bobOption.isVisible().catch(() => false)) await click(alice, bobOption, 300)
const personRole = alice.getByPlaceholder('Name, username, or exact email…').locator('xpath=following::select[1]')
await ring(alice, personRole, 400); await personRole.selectOption('Collaborator'); await unring(alice)
await click(alice, alice.getByRole('button', { name: 'Add', exact: true }).first())
await pause(alice, 1500)
// Person and link share one role picker in 1.19.0, so after adding bob as Collaborator the link
// would be a Collaborator link too; set it back to Viewer before creating it.
const linkRole = alice.locator('.link-intro select')
await ring(alice, linkRole, 500); await linkRole.selectOption('Viewer'); await unring(alice)
timeline.facts.linkRole = await linkRole.inputValue()
await click(alice, alice.getByRole('button', { name: 'Create link' }))
await pause(alice, 1500)
const link = (await alice.locator('.one-time code').innerText()).trim()
await ring(alice, alice.locator('.one-time'), 2500); await unring(alice)

// S6 — the link, opened without an account, then revoked
scene('link')
const anonymous = await C.browser.newContext({ viewport: { width: 1920, height: 1080 } })
const visitor = await anonymous.newPage()
await film('visitor', visitor)
await waiting('link-open', async () => { await visitor.goto(link); await visitor.getByText('Read-only').first().waitFor({ timeout: 90000 }) })
timeline.facts.visitorSees = (await visitor.locator('body').innerText()).match(/(Viewer|Collaborator) · shared by \w+/)?.[0]
await pause(visitor, 6000)
await film('alice', alice)
await pause(alice, 800)
await click(alice, alice.getByRole('button', { name: 'Revoke' }).first(), 900)
await pause(alice, 1500)
await film('visitor', visitor)
await waiting('link-reload', async () => { await visitor.reload(); await visitor.getByText('invalid, expired, or revoked').waitFor({ timeout: 60000 }) })
await pause(visitor, 4000)
timeline.facts.linkRevokedShown = await visitor.getByText('invalid, expired, or revoked').isVisible().catch(() => false)
await anonymous.close()
await film('alice', alice)
await click(alice, alice.locator('.share-head button').last(), 300)

// S7 — bob takes over the running session
scene('takeover')
await bob.goto(HUB)
await bob.waitForTimeout(2500)
await film('bob', bob)
await pause(bob, 2500)
await click(bob, bob.getByText('Issue #42: invoice totals one cent short').first())
await pause(bob, 3000)
const composer = bob.locator('textarea').last()
await type(bob, composer, 'DEMO-TEST Please add a regression test for #42 to the same branch.', 18)
await composer.press('Enter')
await waiting('agent-test', () => bob.getByText('Added a regression test for #42').first().waitFor({ timeout: 300000 }))
await pause(bob, 3500)

// S8 — model switch mid-session
scene('model')
await click(bob, bob.getByRole('button', { name: /Model/ }).first())
await pause(bob, 1500)
await click(bob, bob.getByText('standin-deep', { exact: true }).first(), 500)
await click(bob, bob.getByRole('button', { name: 'Switch', exact: true }))
await waiting('model-switch', () => bob.getByText(/Now on standin-deep/).waitFor({ timeout: 180000 }))
await pause(bob, 2500)
// The conversation shows the CLI's raw /model output after a switch (#515); the edit drops
// everything between these marks, and the answer is scrolled to the top before the cut ends.
mark('cut-start', 'raw-model-output')
await bob.keyboard.press('Escape')
await composer.fill('DEMO-SUMMARY Summarise the change for the reviewer.')
await composer.press('Enter')
const summary = bob.getByText('[stand-in model standin-deep] Summary for the reviewer').first()
await summary.waitFor({ timeout: 300000 })
await summary.evaluate(el => el.scrollIntoView({ block: 'start' }))
await pause(bob, 800)
mark('cut-end', 'raw-model-output')
await ring(bob, summary, 4000); await unring(bob)

// S9 — what bob may do: allowed agents, his budget
scene('limits')
await click(bob, bob.getByRole('button', { name: '+ Give an agent a task' }).first())
await pause(bob, 1500)
await ring(bob, bob.getByText('Agent', { exact: true }).locator('xpath=following::*[1]'), 2500); await unring(bob)
timeline.facts.bobAgents = await bob.getByText('Agent', { exact: true }).locator('xpath=following::*[1]').innerText().catch(() => '')
await click(bob, bob.getByRole('button', { name: 'Cancel', exact: true }))
await click(bob, bob.getByText('Usage & cost').first())
await pause(bob, 4000)
mark('end', 'end')
await cdp?.send('Page.stopScreencast').catch(() => {})
timeline.facts.branch = 'fix/42-rounding'
save()
console.log(JSON.stringify(timeline.facts))
for (const x of [A, B, C]) await x.browser.close().catch(() => {})
