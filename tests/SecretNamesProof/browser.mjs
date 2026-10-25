// #253 browser proof: the branch's Vite app (npx vite --port 5173) in real Chromium against the
// REAL backend on 127.0.0.1:8080 (reset rig, see proof.mjs). Two stored API_KEYs, a project
// selecting one; the Settings list, the add form, the new-session dialog and the project dialog
// show the clash before saving, and the backend refuses the same choice when sent anyway.
// Run: node browser.mjs <chrome-headless-shell> <outdir>
import fs from 'node:fs'
import { createRequire } from 'node:module'

const [chrome, out] = process.argv.slice(2)
const { chromium } = createRequire(import.meta.url)('playwright-core')
const API = 'http://127.0.0.1:8080/api'
const post = async (p, body) => (await fetch(API + p, { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify(body) })).json()
const results = []
const check = (ok, what, detail = '') => { results.push(ok); console.log(`${ok ? 'PASS' : 'FAIL'} ${what}${detail ? ' — ' + detail : ''}`) }

const prod = await post('/secrets', { name: 'API_KEY', delivery: 'env', value: 'prod-value-0001', description: 'production' })
const staging = await post('/secrets', { name: 'API_KEY', delivery: 'env', value: 'staging-value-0002', description: 'staging' })
const project = await post('/projects', { name: 'Deploys', secretIds: [prod.id] })

fs.mkdirSync(out, { recursive: true })
const browser = await chromium.launch({ executablePath: chrome, args: ['--no-sandbox', '--disable-dev-shm-usage'] })
const page = await browser.newPage({ viewport: { width: 1280, height: 1000 } })
const bodies = []
page.on('response', async r => { if (r.url().includes('/api/')) bodies.push(await r.text().catch(() => '')) })

await page.goto('http://localhost:5173/settings/secrets')
await page.waitForSelector('[data-secret-list]')
const ids = await page.$$eval('[data-secret-id]', els => els.map(e => e.textContent.trim()))
check(ids.length === 2 && ids.includes(prod.id) && ids.includes(staging.id), 'the two API_KEY rows show their ids', ids.join(', '))
await page.click('[data-secret-add]')
await page.fill('[data-secret-name]', 'API_KEY')
const namesake = await page.textContent('[data-secret-namesake]')
check(namesake.includes('production (' + prod.id + ')') && namesake.includes('staging (' + staging.id + ')'), 'the add form lists both namesakes', namesake.replace(/\s+/g, ' ').trim())
await page.screenshot({ path: `${out}/settings-namesakes.png`, fullPage: true })
await page.click('[data-secret-cancel]')

// Rename in place: the edit carries the id and the row keeps it.
await page.click(`[data-secret-id="${staging.id}"] ~ .actions [data-secret-edit]`)
await page.fill('[data-secret-name]', 'API_KEY_STAGING')
await page.click('[data-secret-save]')
await page.waitForSelector('[data-secret="API_KEY_STAGING"]')
const renamed = (await (await fetch(API + '/secrets')).json()).find(s => s.id === staging.id)
check(renamed?.name === 'API_KEY_STAGING', 'renaming in Settings keeps the id', JSON.stringify(renamed))
await post('/secrets', { id: staging.id, name: 'API_KEY', delivery: 'env' })

await page.goto('http://localhost:5173/')
await page.click('text=+ Give an agent a task')
await page.waitForSelector(`[data-secret-option="${prod.id}"]`)
await page.check(`[data-secret-option="${prod.id}"]`)
await page.click(`[data-secret-grant="${staging.id}"] [data-grant-level="session"]`)
const conflict = await page.textContent('[data-secret-conflict]')
check(conflict.includes('2 secrets as $API_KEY'), 'the new-session dialog shows the clash', conflict.replace(/\s+/g, ' ').trim())
check(await page.isDisabled('[data-submit]'), 'and Start session is disabled')
await page.locator('[data-secret-conflict]').scrollIntoViewIfNeeded()
await page.screenshot({ path: `${out}/new-session-conflict.png`, fullPage: true })
await page.click(`[data-secret-grant="${staging.id}"] [data-grant-level="none"]`)
check(!(await page.$('[data-secret-conflict]')) && !(await page.isDisabled('[data-submit]')), 'revoking the grant clears it')

await page.click(`[data-project-secrets-open="${project.id}"]`)
await page.check(`[data-project-secret="${staging.id}"]`)
check((await page.textContent('[data-project-secrets] [data-secret-conflict]')).includes('Every session in this project would receive 2 secrets'),
  'the project dialog shows the clash')
check(await page.isDisabled('[data-project-secrets-save]'), 'and Save is disabled')
await page.screenshot({ path: `${out}/project-conflict.png`, fullPage: true })

check(!bodies.some(b => b.includes('value-000')), 'no response the browser received carried a value')
await browser.close()
console.log(`\n${results.filter(Boolean).length}/${results.length} passed`)
process.exit(results.every(Boolean) ? 0 : 1)
