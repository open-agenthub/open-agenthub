// #215 in real Chromium: the branch's web app (Vite on 127.0.0.1:5173) against the real backend,
// kube-apiserver, Postgres and S3 of README.md. Edits a running session's limits, restarts it from
// the dialog, and shows a LimitRange refusal and the "next start" choice.
// Run: node browser.mjs <chrome-headless-shell> <outdir> <admin-kubeconfig>
import { execFileSync } from 'node:child_process'
import fs from 'node:fs'
import { createRequire } from 'node:module'

const [exe, out, adminKubeconfig] = process.argv.slice(2)
const { chromium } = createRequire(import.meta.url)('playwright-core')
fs.mkdirSync(out, { recursive: true })
const NS = 'agenthub-sessions'
const kubectl = (...args) => execFileSync(process.env.KUBECTL || 'kubectl', ['--kubeconfig', adminKubeconfig, '-n', NS, ...args], { encoding: 'utf8' })
const applyYaml = yaml => execFileSync(process.env.KUBECTL || 'kubectl', ['--kubeconfig', adminKubeconfig, '-n', NS, 'apply', '-f', '-'], { input: yaml, encoding: 'utf8' })
async function api(method, p, body) {
  const text = await (await fetch('http://127.0.0.1:8080/api' + p, { method, headers: { 'Content-Type': 'application/json' }, body: body && JSON.stringify(body) })).text()
  return text ? JSON.parse(text) : null
}
const sleep = ms => new Promise(r => setTimeout(r, ms))
const results = []
const check = (ok, what, detail = '') => { results.push(ok); console.log(`${ok ? 'PASS' : 'FAIL'} ${what}${detail ? ' — ' + detail : ''}`) }
const pod = id => { try { return JSON.parse(kubectl('get', 'pod', `session-${id}`, '-o', 'json')) } catch { return null } }
const limits = p => p.spec.containers.find(c => c.name === 'agent').resources.limits
async function running(id, notUid) {
  for (let i = 0; i < 60; i++) {
    const p = pod(id)
    if (p && p.metadata.uid !== notUid && p.status?.phase === 'Running' && !p.metadata.deletionTimestamp) return p
    await sleep(500)
  }
  throw new Error('pod did not come up')
}

const session = await api('POST', '/sessions', { title: 'Resource limits proof', mode: 'Interactive', agent: 'Claude', authMode: 'ApiKey' })
const first = await running(session.id)

const browser = await chromium.launch({ executablePath: exe, args: ['--no-sandbox', '--disable-dev-shm-usage'] })
const page = await browser.newPage({ viewport: { width: 1440, height: 1000 } })
const errors = []
page.on('pageerror', e => errors.push(String(e)))
// The Sessions overview, where each row shows the effective request / limit.
async function overview(expected) {
  await page.goto('http://127.0.0.1:5173/')
  await page.click('.sessions-head')
  await page.waitForSelector('[data-session-resources]', { timeout: 20000 })
  for (let i = 0; i < 20 && !(await page.textContent('[data-session-resources]')).includes(expected); i++) {
    await sleep(500); await page.click('.sessions-head')
  }
  return page.textContent('[data-session-resources]')
}
async function openEdit() {
  await page.goto(`http://127.0.0.1:5173/s/${session.id}`)
  await page.waitForSelector('button:has-text("Edit session")', { timeout: 20000 })
  await page.click('button:has-text("Edit session")')
  await page.waitForSelector('[data-advanced]')
  await page.click('[data-advanced]')
  await page.waitForSelector('[data-session-resources-fields]')
}

check((await overview('CPU 500m / 2 · memory 1Gi / 4Gi')).includes('CPU 500m / 2 · memory 1Gi / 4Gi'), 'the Sessions overview shows the effective request / limit')
await page.locator('.row').first().screenshot({ path: `${out}/1-overview.png` })

await openEdit()
check((await page.textContent('[data-session-resources-effective]')).includes('CPU 500m / 2 · memory 1Gi / 4Gi'), 'the edit dialog shows the values in effect')
check(await page.getAttribute('[data-resource="memoryLimit"]', 'placeholder') === 'default (4Gi)', 'an empty limit is labelled with the default it resolves to')
await page.fill('[data-resource="memory"]', '2Gi')
await page.fill('[data-resource="memoryLimit"]', '8Gi')
await page.waitForSelector('[data-resources-restart]')
check((await page.textContent('[data-resources-restart]')).includes('the conversation continues'), 'changing them offers the restart, with what it does')
check((await page.textContent('[data-submit]')).trim() === 'Save and restart', 'the button says it restarts')
await page.locator('[data-session-resources-fields]').screenshot({ path: `${out}/2-fields.png` })
await page.locator('[data-resources-restart]').screenshot({ path: `${out}/3-restart-note.png` })
await page.screenshot({ path: `${out}/4-edit-dialog.png`, fullPage: true })
await page.click('[data-submit]')
const second = await running(session.id, first.metadata.uid)
check(limits(second).memory === '8Gi', 'saving restarted the session onto a pod with the new limit', JSON.stringify(limits(second)))
check(second.spec.containers[0].env.find(e => e.name === 'AGENTHUB_RESUME')?.value === '1', 'the new pod resumes the conversation')
check((await overview('memory 2Gi / 8Gi')).includes('memory 2Gi / 8Gi'), 'the overview follows')
await page.locator('.row').first().screenshot({ path: `${out}/5-overview-after.png` })

// A LimitRange the new value breaks: the dialog stays open with the API server's reason.
applyYaml(`apiVersion: v1
kind: LimitRange
metadata: { name: proof-max }
spec: { limits: [ { type: Container, max: { memory: 10Gi } } ] }`)
await openEdit()
await page.fill('[data-resource="memoryLimit"]', '12Gi')
await page.click('[data-submit]')
await page.waitForSelector('.err')
const refusal = await page.textContent('.err')
check(refusal.includes('Saved, but the session was not restarted') && refusal.includes('maximum memory usage per Container is 10Gi'),
  'a refused restart keeps the dialog open with the reason', refusal.slice(0, 160))
check(pod(session.id).metadata.uid === second.metadata.uid, 'the session kept running on its pod')
await page.screenshot({ path: `${out}/6-refused.png`, fullPage: true })
kubectl('delete', 'limitrange', 'proof-max')

// Choosing the next start saves without restarting; the header says the pod still has the old values.
await openEdit()
await page.fill('[data-resource="memoryLimit"]', '6Gi')
await page.uncheck('[data-resources-restart-now]')
check((await page.textContent('[data-submit]')).trim() === 'Save changes', 'unticked, the button only saves')
await page.click('[data-submit]')
await sleep(1500)
check(pod(session.id).metadata.uid === second.metadata.uid, 'nothing was restarted')
check((await overview('next restart')).includes('memory 2Gi / 6Gi · applies at the next restart'), 'the overview marks the values as pending a restart')
await page.locator('.row').first().screenshot({ path: `${out}/7-overview-pending.png` })
await openEdit()
check((await page.textContent('[data-session-resources-effective]')).includes('the running pod still has its previous values'), 'so does the edit dialog')
await page.locator('[data-session-resources-fields]').screenshot({ path: `${out}/7b-fields-pending.png` })

// The new-session dialog at a phone width: four fields, two per row.
await page.setViewportSize({ width: 390, height: 844 })
await page.goto('http://127.0.0.1:5173/')
await page.click('button:has-text("Give an agent a task")')
await page.waitForSelector('h3:has-text("New session")')
if (!(await page.$('[data-session-resources-fields]'))) await page.click('[data-advanced]')
await page.waitForSelector('[data-session-resources-fields]', { timeout: 10000 })
const fields = await page.$('[data-session-resources-fields]')
if (fields) {
  await fields.screenshot({ path: `${out}/8-new-dialog-390.png` })
  const overflow = await page.evaluate(() => document.documentElement.scrollWidth > window.innerWidth)
  check(!overflow, 'the new-session dialog has no horizontal overflow at 390 px')
} else check(false, 'the new-session dialog shows the resource fields')

check(errors.length === 0, 'no page errors', errors.join(' | '))
await browser.close()
await api('DELETE', `/sessions/${session.id}`)
console.log(`${results.filter(Boolean).length}/${results.length} passed`)
process.exit(results.every(Boolean) ? 0 : 1)
