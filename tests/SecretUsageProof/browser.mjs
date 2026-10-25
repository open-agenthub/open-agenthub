// #254 proof: Settings → Secrets shows where each secret is used, against the REAL backend (dev
// auth) on 127.0.0.1:8080 with real Postgres and a real API server (either rig of
// tests/SecretNamesProof or tests/SecretLiveSyncProof), in real Chromium on the branch's Vite app
// (npx vite --port 5173). Every way a session can hold a secret is produced through the real
// endpoints: a project selection, an explicit selection, an owner grant passed to children, a
// child spawned through the agent's own spawn endpoint, and an answered agent request.
// Run: node browser.mjs <chrome-headless-shell> <outdir> [postgres-url]
import fs from 'node:fs'
import { createRequire } from 'node:module'

const [chrome, out, pgUrl = 'postgres://postgres@127.0.0.1:54329/agenthub255'] = process.argv.slice(2)
const here = createRequire(import.meta.url)
const { chromium } = here('playwright-core')
const pg = here('pg')
const BASE = 'http://127.0.0.1:8080'
async function call(method, path, body, headers = {}) {
  const r = await fetch(BASE + path, { method, headers: { 'Content-Type': 'application/json', ...headers }, body: body && JSON.stringify(body) })
  const text = await r.text()
  if (!r.ok) throw new Error(`${method} ${path}: ${r.status} ${text}`)
  return text ? JSON.parse(text) : null
}
const results = []
const check = (ok, what, detail = '') => { results.push(ok); console.log(`${ok ? 'PASS' : 'FAIL'} ${what}${detail ? ' — ' + detail : ''}`) }
const base = { mode: 'Interactive', agent: 'Claude', authMode: 'ApiKey' }

const apiKey = await call('POST', '/api/secrets', { name: 'API_KEY', delivery: 'env', value: 'usage-env-0001', description: 'production' })
const kube = await call('POST', '/api/secrets', { name: 'kube.yaml', delivery: 'file', value: 'usage-file-0002' })
const unused = await call('POST', '/api/secrets', { name: 'UNUSED', delivery: 'env', value: 'usage-env-0003' })
const project = await call('POST', '/api/projects', { name: 'Deploys', secretIds: [apiKey.id] })
const inProject = await call('POST', '/api/sessions', { ...base, title: 'deploy-bot', projectId: project.id })
const selected = await call('POST', '/api/sessions', { ...base, title: 'picked', secretIds: [apiKey.id] })
const parent = await call('POST', '/api/sessions', { ...base, title: 'parent', secretGrants: [{ secretId: kube.id, inheritToChildren: true }] })
const asker = await call('POST', '/api/sessions', { ...base, title: 'asker' })

const db = new pg.Client({ connectionString: pgUrl })
await db.connect()
const token = async id => (await db.query('select callback_token from sessions where id = $1', [id])).rows[0].callback_token
const parentToken = await token(parent.id)
const askerToken = await token(asker.id)
await db.end()
// The child is spawned the way an agent does it, so its grant is the hub's own "inherited".
const child = await call('POST', `/internal/sessions/${parent.id}/spawn`, { ...base, title: 'child' }, { 'X-Agent-Token': parentToken })
const request = await call('POST', `/internal/sessions/${asker.id}/secret-requests`, { name: 'kube.yaml', delivery: 'file', purpose: 'proof' },
  { 'X-Agent-Token': askerToken })
await call('POST', `/api/secret-requests/${request.id}/provide`, { secretId: kube.id })

fs.mkdirSync(out, { recursive: true })
const browser = await chromium.launch({ executablePath: chrome, args: ['--no-sandbox', '--disable-dev-shm-usage'] })
const page = await browser.newPage({ viewport: { width: 1280, height: 900 } })
const bodies = []
page.on('response', async r => { if (r.url().includes('/api/')) bodies.push(await r.text().catch(() => '')) })
await page.goto('http://localhost:5173/settings/secrets')
await page.waitForSelector(`[data-secret-usage="${apiKey.id}"] [data-usage-session]`)

const line = async id => (await page.textContent(`[data-secret-usage="${id}"]`)).replace(/\s+/g, ' ').trim()
const apiLine = await line(apiKey.id)
check(apiLine.includes('Projects: Deploys') && apiLine.includes('deploy-bot (via project Deploys)') && apiLine.includes('picked (selected for it)')
  && !apiLine.includes('passed to child sessions'),
  'API_KEY: the project, a session following it and a session selecting it', apiLine)
const kubeLine = await line(kube.id)
check(kubeLine.includes('parent (granted directly, passed to child sessions)') && kubeLine.includes('child (inherited from its parent')
  && kubeLine.includes('asker (requested by the agent)') && !kubeLine.includes('Projects:'),
  'kube.yaml: a direct grant, the spawned child that inherited it, the session that asked for it', kubeLine)
check((await line(unused.id)) === 'Not used by any project or session.', 'UNUSED says so')
await page.screenshot({ path: `${out}/secret-usage.png`, fullPage: true })

await page.click(`[data-secret="API_KEY"] [data-secret-remove]`)
check((await page.textContent('[data-secret="API_KEY"] [data-secret-remove]')).trim() === 'Really remove? 2 sessions lose it',
  'the armed Remove button says how many sessions lose it')
await page.click('[data-secret="API_KEY"] [data-secret-keep]')

await page.click(`[data-usage-session="${child.id}"]`)
await page.waitForURL(`**/s/${child.id}`)
await page.waitForSelector('[data-session-secrets]')
check(true, 'a session link opens that session inside the app', page.url())
await page.goBack()
await page.waitForSelector(`[data-usage-project="${project.id}"]`)
await page.click(`[data-usage-project="${project.id}"]`)
await page.waitForSelector('[data-project-secrets]')
check(await page.isChecked(`[data-project-secret="${apiKey.id}"]`), 'a project link opens its secret selection')
await page.uncheck(`[data-project-secret="${apiKey.id}"]`)
await page.click('[data-project-secrets-save]')
await page.waitForSelector('[data-project-secrets]', { state: 'detached' })
await page.waitForFunction(id => !document.querySelector(`[data-secret-usage="${id}"]`)?.textContent.includes('Deploys'), apiKey.id)
check(!(await line(apiKey.id)).includes('deploy-bot'), 'narrowing the project there updates the list at once', await line(apiKey.id))
await page.screenshot({ path: `${out}/secret-usage-after-narrowing.png`, fullPage: true })

check(!bodies.some(b => b.includes('usage-env-000') || b.includes('usage-file-000')), 'no response the browser received carried a value')
await browser.close()
console.log(`\n${results.filter(Boolean).length}/${results.length} passed`)
process.exit(results.every(Boolean) ? 0 : 1)
