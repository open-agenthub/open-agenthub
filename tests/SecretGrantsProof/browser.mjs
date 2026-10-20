// Real Chromium against the branch's Vite app and the REAL backend (see README.md): the
// three-way grant control in the session view, edit, new and duplicate dialogs, at desktop and
// phone width. Every grant is read back from the hub's API and from the Kubernetes secret the
// pod's files come from, not from the page.
// Run: node browser.mjs <chrome-headless-shell> <outdir> <sessionId> <kubectl> <kubeconfig>
import { createRequire } from 'node:module'
import { execFileSync } from 'node:child_process'
import fs from 'node:fs'
const require = createRequire(import.meta.url)
const { chromium } = require('playwright-core')

const [exe, out, sessionId, kubectl, kubeconfig] = process.argv.slice(2)
const UI = process.env.UI || 'http://127.0.0.1:5173', API = process.env.API || 'http://127.0.0.1:8080'
fs.mkdirSync(out, { recursive: true })
const results = []
const check = (name, ok, detail = '') => { results.push({ name, ok: !!ok, detail }); console.log(`${ok ? 'PASS' : 'FAIL'} ${name}${detail ? ' — ' + detail : ''}`) }
const api = async path => (await fetch(API + path)).json()
const grantsOf = async id => (await api(`/api/sessions/${id}`)).secretGrants.map(g => `${g.secretId}:${g.inheritToChildren}:${g.source}`).sort()
const fileKeys = id => Object.keys(JSON.parse(execFileSync(kubectl, ['--kubeconfig', kubeconfig, '-n', 'agenthub-sessions',
  'get', 'secret', `sessionsecrets-${id}`, '-o', 'json'], { encoding: 'utf8' })).data || {})
const until = async (fn, what, ms = 10_000) => {
  const end = Date.now() + ms
  for (;;) { const v = await fn(); if (v) return v; if (Date.now() > end) throw new Error('timeout: ' + what); await new Promise(r => setTimeout(r, 200)) }
}

const secrets = await api('/api/secrets')
const idOf = name => secrets.find(s => s.name === name).id
const FILE = idOf('deploy.json'), ENV = idOf('DEPLOY_TOKEN'), PROJ = idOf('PROJECT_KEY')
// A rerun starts from no grants, so every check below sees only its own step's change.
await fetch(`${API}/api/sessions/${sessionId}`, { method: 'PATCH', headers: { 'content-type': 'application/json' }, body: JSON.stringify({ secretGrants: [] }) })

const browser = await chromium.launch({ executablePath: exe, args: ['--no-sandbox', '--disable-dev-shm-usage'] })
const errors = []
async function open(viewport, path) {
  const page = await browser.newPage({ viewport })
  page.on('pageerror', e => errors.push(String(e)))
  page.on('console', m => { if (m.type() === 'error') errors.push(m.text()) })
  await page.goto(UI + path)
  return page
}
const row = (page, id) => page.locator(`[data-secret-grant="${id}"]`)
const checkedLevel = async (page, id) => row(page, id).locator('[aria-checked="true"]').getAttribute('data-grant-level')

// 1. Session view, desktop: keyboard and pointer, each choice saved live.
let page = await open({ width: 1280, height: 900 }, `/s/${sessionId}`)
await page.locator('[data-session-secrets] summary').click()
await row(page, FILE).waitFor()
check('session view: radiogroup per stored secret', await page.locator('[data-session-secrets] [role="radiogroup"]').count() === 3)
check('session view: project-only secret labelled "project"', (await page.locator(`[data-secret-provenance="${PROJ}"]`).textContent()).trim() === 'project')
check('session view: ungranted secret starts at Not allowed', await checkedLevel(page, FILE) === 'none')
check('session view: no inherit checkbox left', await page.locator('[data-secret-inherit], [data-session-secrets] input[type=checkbox]').count() === 0)
await page.screenshot({ path: `${out}/1-session-view-desktop-before.png` })

await row(page, FILE).locator('[aria-checked="true"]').focus()
let patch = page.waitForResponse(r => r.url().endsWith(`/api/sessions/${sessionId}`) && r.request().method() === 'PATCH')
await page.keyboard.press('ArrowRight')
check('keyboard ArrowRight saves "This session"', (await patch).status() === 200)
check('hub: grant stored without inheritance', JSON.stringify(await grantsOf(sessionId)) === JSON.stringify([`${FILE}:false:granted`]), JSON.stringify(await grantsOf(sessionId)))
check('pod files: deploy.json delivered live', await until(() => fileKeys(sessionId).includes('deploy.json'), 'file key').catch(() => false), JSON.stringify(fileKeys(sessionId)))
check('focus follows the selection', await page.evaluate(() => document.activeElement?.dataset.grantLevel) === 'session')
await until(async () => (await page.locator(`[data-secret-provenance="${FILE}"]`).textContent()).trim() === 'granted', 'provenance label')
check('provenance label from the hub: granted', true)

patch = page.waitForResponse(r => r.url().endsWith(`/api/sessions/${sessionId}`) && r.request().method() === 'PATCH')
await page.keyboard.press('ArrowRight')
await patch
check('hub: "Also child sessions" stores inheritToChildren', JSON.stringify(await grantsOf(sessionId)) === JSON.stringify([`${FILE}:true:granted`]))
await page.screenshot({ path: `${out}/2-session-view-desktop-children.png` })

patch = page.waitForResponse(r => r.url().endsWith(`/api/sessions/${sessionId}`) && r.request().method() === 'PATCH')
await row(page, FILE).locator('[data-grant-level="none"]').click()
await patch
check('hub: "Not allowed" revokes', (await grantsOf(sessionId)).length === 0)
check('pod files: deploy.json removed live', await until(() => !fileKeys(sessionId).includes('deploy.json'), 'file key gone').catch(() => false), JSON.stringify(fileKeys(sessionId)))
check('project secret still effective after revoking grants', (await api(`/api/sessions/${sessionId}`)).effectiveSecrets.some(s => s.id === PROJ && s.provenance === 'project'))

// 2. Edit dialog: same control, saved with the dialog.
await page.locator('button', { hasText: 'Edit session' }).click()
const dialog = page.locator('[data-secret-grants]').last()
await dialog.locator(`[data-secret-grant="${ENV}"] [data-grant-level="children"]`).click()
check('edit dialog: three-way control present', await dialog.locator('[role="radio"]').count() === 9)
await dialog.screenshot({ path: `${out}/3-edit-dialog.png` })
await page.locator('[data-submit]', { hasText: 'Save changes' }).click()
await until(async () => (await grantsOf(sessionId)).includes(`${ENV}:true:granted`), 'edit save')
check('edit dialog: saved "Also child sessions"', true)
await page.close()

// 3. Phone width: no horizontal overflow, every option on screen.
page = await open({ width: 390, height: 844 }, `/s/${sessionId}`)
await page.locator('[data-session-secrets] summary').click()
await row(page, FILE).waitFor()
const overflow = await page.evaluate(() => {
  const groups = [...document.querySelectorAll('[data-session-secrets] [role="radio"]')].map(b => b.getBoundingClientRect())
  return { scroll: document.documentElement.scrollWidth, width: innerWidth, outside: groups.filter(r => r.left < 0 || r.right > innerWidth).length }
})
check('phone 390px: no horizontal scroll and every option inside the viewport', overflow.scroll <= overflow.width && overflow.outside === 0, JSON.stringify(overflow))
check('phone: inherited choice shown as "Also child sessions"', await checkedLevel(page, ENV) === 'children')
await page.locator('[data-session-secrets]').screenshot({ path: `${out}/4-session-view-phone.png` })
await page.close()

// 4. New session: grant starts at Not allowed; "This session" creates a non-inheritable grant.
page = await open({ width: 1280, height: 900 }, '/')
await page.locator('button.task-btn').click()
const picker = page.locator('[data-secret-grants]')
await picker.waitFor()
check('new dialog: every secret starts at Not allowed', (await picker.locator('[aria-checked="true"]').evaluateAll(b => b.map(x => x.dataset.grantLevel))).every(l => l === 'none'))
await picker.locator(`[data-secret-grant="${FILE}"] [data-grant-level="session"]`).click()
await picker.screenshot({ path: `${out}/5-new-dialog.png` })
const created = page.waitForResponse(r => r.url().endsWith('/api/sessions') && r.request().method() === 'POST')
await page.locator('[data-submit]', { hasText: 'Start session' }).click()
const newId = (await (await created).json()).id
check('new dialog: created with a non-inheritable grant', JSON.stringify(await grantsOf(newId)) === JSON.stringify([`${FILE}:false:granted`]), JSON.stringify(await grantsOf(newId)))

// 5. Duplicate: same control, starts without grants (#209).
await page.goto(UI + '/')
// The row's action buttons sit under its title until hovered; the dialog is what is under test.
await page.locator('[aria-label="Duplicate session"]').first().dispatchEvent('click')
const dup = page.locator('[data-secret-grants]')
await dup.waitFor()
check('duplicate dialog: three-way control, no grant copied', await dup.locator('[role="radiogroup"]').count() === 3
  && (await dup.locator('[aria-checked="true"]').evaluateAll(b => b.map(x => x.dataset.grantLevel))).every(l => l === 'none'))
await dup.screenshot({ path: `${out}/6-duplicate-dialog.png` })
await page.close()

check('no page errors', errors.length === 0, errors.join(' | '))
await browser.close()
fs.writeFileSync(`${out}/results.json`, JSON.stringify(results, null, 2))
process.exit(results.every(r => r.ok) ? 0 : 1)
