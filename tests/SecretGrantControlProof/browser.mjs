// #329 proof: one secret control per place, against the REAL backend (dev auth) on 127.0.0.1:8080
// with real Postgres and a real API server (the rig of tests/SecretUsageProof), in real Chromium on
// the branch's Vite app (npx vite --port 5173).
//
// 1. Sessions created through the API with every kind of stored state open in the edit dialog at
//    the level the mapping says, with one control and no checkboxes; saving untouched sends
//    neither field and leaves the stored state as it was.
// 2. Changes in the edit dialog, the session view and the project dialog persist, and reach the
//    session's copies (sessionsecrets-<id>, sessionenv-<id>) at once (#255).
// Run: node browser.mjs <chrome-headless-shell> <outdir> <kubectl> <admin kubeconfig> [baseline]
//   "baseline" against main only counts the controls in the edit dialog and takes a screenshot.
import fs from 'node:fs'
import path from 'node:path'
import { execFileSync } from 'node:child_process'
import { createRequire } from 'node:module'

const [chrome, out, kubectl, kubeconfig, mode] = process.argv.slice(2)
const { chromium } = createRequire(import.meta.url)('playwright-core')
const BASE = 'http://127.0.0.1:8080', UI = 'http://localhost:5173'
fs.mkdirSync(out, { recursive: true })
const results = []
const check = (ok, what, detail = '') => { results.push(ok); console.log(`${ok ? 'PASS' : 'FAIL'} ${what}${detail ? ' — ' + detail : ''}`) }
async function call(method, p, body) {
  const r = await fetch(BASE + p, { method, headers: { 'Content-Type': 'application/json' }, body: body && JSON.stringify(body) })
  const text = await r.text()
  if (!r.ok) throw new Error(`${method} ${p}: ${r.status} ${text}`)
  return text ? JSON.parse(text) : null
}
const k = (...a) => execFileSync(kubectl, ['--kubeconfig', kubeconfig, '-n', 'agenthub-sessions', ...a], { encoding: 'utf8' })
const copyKeys = name => { try { return Object.keys(JSON.parse(k('get', 'secret', name, '-o', 'json')).data || {}).sort() } catch { return null } }
const sleep = ms => new Promise(r => setTimeout(r, ms))
const run = Date.now().toString(36)
const base = { mode: 'Interactive', agent: 'Claude', authMode: 'ApiKey' }

const A = await call('POST', '/api/secrets', { name: `A_${run}`, delivery: 'env', value: `grant-a-${run}` })
const F = await call('POST', '/api/secrets', { name: `f-${run}.json`, delivery: 'file', value: `grant-f-${run}` })
const X = await call('POST', '/api/secrets', { name: `x-${run}.pem`, delivery: 'file', value: `grant-x-${run}` })
const project = await call('POST', '/api/projects', { name: `Deploys ${run}`, secretIds: [A.id, F.id] })
// Existing states: following the project plus a grant; narrowed plus an inheriting grant; outside
// a project with an explicit selection.
const s1 = await call('POST', '/api/sessions', { ...base, title: `follows ${run}`, projectId: project.id, secretGrants: [{ secretId: X.id, inheritToChildren: false }] })
const s2 = await call('POST', '/api/sessions', { ...base, title: `narrowed ${run}`, projectId: project.id, secretIds: [A.id], secretGrants: [{ secretId: F.id, inheritToChildren: true }] })
const s3 = await call('POST', '/api/sessions', { ...base, title: `outside ${run}`, secretIds: [X.id] })
const expected = {
  [s1.id]: { [A.id]: 'children', [F.id]: 'children', [X.id]: 'session' },
  [s2.id]: { [A.id]: 'children', [F.id]: 'children', [X.id]: 'none' },
  [s3.id]: { [A.id]: 'none', [F.id]: 'none', [X.id]: 'children' }
}
const stored = async id => { const s = await call('GET', `/api/sessions/${id}`); return JSON.stringify([s.secretIds ?? null, s.secretGrants || []]) }

const browser = await chromium.launch({ executablePath: chrome, args: ['--no-sandbox', '--disable-dev-shm-usage'] })
const page = await browser.newPage({ viewport: { width: 1360, height: 1000 } })
const errors = [], patches = []
page.on('pageerror', e => errors.push(String(e)))
page.on('request', r => { if (r.method() === 'PATCH' || (r.method() === 'PUT' && r.url().includes('/api/'))) patches.push({ url: r.url(), body: r.postData() }) })

async function openEdit(session) {
  await page.goto(`${UI}/s/${session.id}`)
  await page.click('button:has-text("Edit session")')
  await page.waitForSelector('[data-submit]')
  await sleep(500)
}
const level = (id) => page.getAttribute(`[data-secret-grant="${id}"] [aria-checked="true"]`, 'data-grant-level')

if (mode === 'baseline') {
  await openEdit(s1)
  const boxes = await page.locator('[data-secret-option]').count(), groups = await page.locator('[data-secret-grant]').count()
  console.log(`main: ${boxes} selection checkboxes and ${groups} grant button groups in one edit dialog`)
  await page.locator('[data-secret-grants]').scrollIntoViewIfNeeded()
  await page.screenshot({ path: path.join(out, 'main-edit-dialog.png') })
  await browser.close()
  process.exit(0)
}

// ---- 1. existing states map 1:1, untouched saves send nothing
for (const session of [s1, s2, s3]) {
  await openEdit(session)
  check(await page.locator('[data-secret-option]').count() === 0 && await page.locator('[data-secret-grants] input[type="checkbox"]').count() === 0,
    `${session.title}: no selection checkboxes in the edit dialog`)
  const shown = {}
  for (const id of [A.id, F.id, X.id]) shown[id] = await level(id)
  check(JSON.stringify(shown) === JSON.stringify(expected[session.id]), `${session.title}: each secret shows the level its stored state maps to`, JSON.stringify(Object.values(shown)))
  if (session === s1) {
    await page.locator('[data-secret-grants]').scrollIntoViewIfNeeded()
    await page.screenshot({ path: path.join(out, '1-edit-dialog-one-control.png') })
  }
  const before = await stored(session.id)
  patches.length = 0
  await page.click('[data-submit]')
  await sleep(1500)
  const body = JSON.parse(patches.find(p => p.url.includes(`/sessions/${session.id}`))?.body || '{}')
  check(!('secretIds' in body) && !('secretGrants' in body), `${session.title}: saving untouched sends neither field`, JSON.stringify(Object.keys(body)))
  check(await stored(session.id) === before, `${session.title}: the stored selection and grants are unchanged`, before)
}

// ---- 2a. the edit dialog: F of s1 to "This session"
await openEdit(s1)
check(await page.locator('[data-secret-follow-note]').count() === 0, 'edit dialog: a session following its project shows no note')
await page.click(`[data-secret-grant="${F.id}"] [data-grant-level="session"]`)
check(await page.locator('[data-secret-follow-note]').count() === 1, 'edit dialog: lowering a project secret says the session stops following the project')
check(await level(F.id) === 'session', 'edit dialog: the row shows the level just chosen')
await page.locator('[data-secret-grants]').scrollIntoViewIfNeeded()
await page.mouse.move(0, 0)
await sleep(400)
await page.screenshot({ path: path.join(out, '1b-edit-dialog-follow-note.png') })
patches.length = 0
await page.click('[data-submit]')
await sleep(1500)
let s = await call('GET', `/api/sessions/${s1.id}`)
check(JSON.stringify(s.secretIds) === JSON.stringify([A.id]) && s.secretGrants.some(g => g.secretId === F.id && !g.inheritToChildren) && s.secretGrants.some(g => g.secretId === X.id && !g.inheritToChildren),
  'edit dialog: "This session" narrows the selection and grants without inheritance', JSON.stringify([s.secretIds, s.secretGrants]))
check(JSON.stringify(copyKeys(`sessionsecrets-${s1.id}`)) === JSON.stringify([F.name, X.name].sort()), 'edit dialog: the file copy still holds both files', JSON.stringify(copyKeys(`sessionsecrets-${s1.id}`)))
await openEdit(s1)
check(await level(F.id) === 'session' && await level(A.id) === 'children', 'edit dialog: the change reads back after a reload')
await page.click(`[data-secret-grant="${F.id}"] [data-grant-level="none"]`)
await page.click('[data-submit]')
await sleep(1500)
check(JSON.stringify(copyKeys(`sessionsecrets-${s1.id}`)) === JSON.stringify([X.name]), 'edit dialog: "Not allowed" takes the file out of the running session\'s copy', JSON.stringify(copyKeys(`sessionsecrets-${s1.id}`)))

// ---- 2b. the session view: X of s3 to "Not allowed", A to "This session"
await page.goto(`${UI}/s/${s3.id}`)
await page.click('[data-session-secrets] summary')
await page.waitForSelector(`[data-session-secrets] [data-secret-grant="${X.id}"]`)
await page.screenshot({ path: path.join(out, '2-session-view-one-control.png') })
await page.click(`[data-session-secrets] [data-secret-grant="${X.id}"] [data-grant-level="none"]`)
await sleep(1500)
await page.click(`[data-session-secrets] [data-secret-grant="${A.id}"] [data-grant-level="session"]`)
await sleep(1500)
s = await call('GET', `/api/sessions/${s3.id}`)
check(JSON.stringify(s.secretIds) === '[]' && JSON.stringify(s.secretGrants.map(g => [g.secretId, g.inheritToChildren])) === JSON.stringify([[A.id, false]]),
  'session view: each choice is saved at once', JSON.stringify([s.secretIds, s.secretGrants]))
check(JSON.stringify(copyKeys(`sessionsecrets-${s3.id}`)) === '[]' && JSON.stringify(copyKeys(`sessionenv-${s3.id}`)) === JSON.stringify([A.name]),
  'session view: the copies follow (file gone, variable in)', JSON.stringify([copyKeys(`sessionsecrets-${s3.id}`), copyKeys(`sessionenv-${s3.id}`)]))
await page.reload()
await page.click('[data-session-secrets] summary')
await page.waitForSelector(`[data-session-secrets] [data-secret-grant="${X.id}"]`)
check(await page.getAttribute(`[data-session-secrets] [data-secret-grant="${X.id}"] [aria-checked="true"]`, 'data-grant-level') === 'none'
  && await page.getAttribute(`[data-session-secrets] [data-secret-grant="${A.id}"] [aria-checked="true"]`, 'data-grant-level') === 'session',
  'session view: the choices read back after a reload')

// ---- 2c. the project dialog: the same control with two levels
await page.goto(`${UI}/settings/secrets`)
await page.waitForSelector(`[data-usage-project="${project.id}"]`)
await page.click(`[data-usage-project="${project.id}"]`)
await page.waitForSelector(`[data-project-secret="${F.id}"] [role="radio"]`)
check(await page.locator('[data-project-secrets] input[type="checkbox"]').count() === 0, 'project dialog: no checkboxes')
const labels = await page.locator(`[data-project-secret="${F.id}"] [role="radio"]`).allTextContents()
check(JSON.stringify(labels) === JSON.stringify(['Not allowed', 'Sessions in this project']), 'project dialog: the segmented control with the project\'s two levels', JSON.stringify(labels))
check(await page.getAttribute(`[data-project-secret="${F.id}"] [aria-checked="true"]`, 'data-grant-level') === 'project'
  && await page.getAttribute(`[data-project-secret="${X.id}"] [aria-checked="true"]`, 'data-grant-level') === 'none', 'project dialog: the stored selection maps 1:1')
await page.screenshot({ path: path.join(out, '3-project-dialog.png') })
await page.click(`[data-project-secret="${F.id}"] [data-grant-level="none"]`)
await page.click('[data-project-secrets-save]')
await page.waitForSelector('[data-project-secrets]', { state: 'detached' })
const p = (await call('GET', '/api/projects')).find(x => x.id === project.id)
check(JSON.stringify(p.secretIds) === JSON.stringify([A.id]), 'project dialog: the narrowed selection persists', JSON.stringify(p.secretIds))
// s2 holds F through its own inheriting grant, which the project does not take away.
check(JSON.stringify(copyKeys(`sessionsecrets-${s2.id}`)) === JSON.stringify([F.name]), 'project dialog: a session holding F by grant keeps it', JSON.stringify(copyKeys(`sessionsecrets-${s2.id}`)))

check(errors.length === 0, 'no page errors', errors.join(' | '))
await browser.close()
for (const id of [s1.id, s2.id, s3.id]) await call('DELETE', `/api/sessions/${id}`).catch(() => {})
console.log(`${results.filter(Boolean).length}/${results.length} passed`)
process.exit(results.every(Boolean) ? 0 : 1)
