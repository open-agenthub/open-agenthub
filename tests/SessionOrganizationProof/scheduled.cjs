// #295: a scheduled session moved between projects. The real CronJob and Job controllers start the
// runs; the next run's pod must reference the destination's variables and none of the source's.
const { chromium } = require(process.env.PLAYWRIGHT_PATH || '/opt/session-agent/node_modules/playwright-core')
const { execFileSync } = require('node:child_process')
const assert = require('node:assert/strict')
const kubectl = (...args) => execFileSync(process.env.KUBECTL, ['--kubeconfig', process.env.PROOF_KUBECONFIG, '-n', 'agenthub-sessions', ...args], { encoding: 'utf8' })
const get = (...args) => JSON.parse(kubectl('get', ...args, '-o', 'json'))
const sleep = ms => new Promise(r => setTimeout(r, ms))
const log = (...a) => console.log(new Date().toISOString(), ...a)
async function until(what, check, ms = 150000) {
  const end = Date.now() + ms
  for (;;) { const v = check(); if (v) return v; if (Date.now() > end) throw new Error('timed out: ' + what); await sleep(2000) }
}
const decode = secret => Object.fromEntries(Object.entries(secret.data || {}).map(([k, v]) => [k, Buffer.from(v, 'base64').toString()]))
const cron = id => get('cronjob', 'session-' + id)
const runPods = id => get('pod', '-l', 'agenthub.dev/session=' + id + ',agenthub.dev/component=agent').items
const runJobs = id => get('job').items.filter(j => j.metadata.ownerReferences?.some(o => o.name === 'session-' + id))
const agentEnv = spec => spec.containers.find(c => c.name === 'agent').env
// What the container would receive: each secretKeyRef resolved against the secret as it is now.
function resolvedEnv(pod) {
  const out = {}
  for (const e of agentEnv(pod.spec)) {
    const ref = e.valueFrom?.secretKeyRef
    if (!ref) { out[e.name] = e.value; continue }
    const data = decode(get('secret', ref.name))
    if (ref.key in data) out[e.name] = data[ref.key]
  }
  return out
}

;(async () => {
  const browser = await chromium.connectOverCDP(process.env.CDP_ENDPOINT)
  const context = await browser.newContext({ viewport: { width: 1440, height: 1000 } })
  const page = await context.newPage(); const errors = []; page.on('pageerror', e => errors.push(e.message))
  await page.goto(process.env.PROOF_URL)
  await page.getByRole('button', { name: /Give an agent a task/ }).first().waitFor()
  const api = (path, method = 'GET', body, owner = 'proof-owner') => page.evaluate(async ({ path, method, body, owner }) => {
    const r = await fetch('/api/' + path, { method, headers: { 'Content-Type': 'application/json', 'X-Proof-Owner': owner }, body: body ? JSON.stringify(body) : undefined })
    const text = await r.text(); let data; try { data = JSON.parse(text) } catch { data = text }
    return { status: r.status, body: data }
  }, { path, method, body, owner })

  const n = Date.now()
  const OLD = 'synthetic-old-' + n, NEW = 'synthetic-new-' + n
  const secret = async (name, delivery, value) => (await api('secrets', 'POST', { name: name + '_' + n, delivery, value, description: 'proof' })).body
  const oldEnv = await secret('OLD_PROJECT_ENV', 'env', OLD), oldFile = await secret('OLD_PROJECT_FILE', 'file', OLD + '-file')
  const newEnv = await secret('NEW_PROJECT_ENV', 'env', NEW), newFile = await secret('NEW_PROJECT_FILE', 'file', NEW + '-file')
  const source = (await api('projects', 'POST', { name: 'Source ' + n, color: null, secretIds: [oldEnv.id, oldFile.id] })).body
  const target = (await api('projects', 'POST', { name: 'Target ' + n, color: null, secretIds: [newEnv.id, newFile.id] })).body
  const foreign = (await api('projects', 'POST', { name: 'Foreign ' + n, color: null }, 'proof-other')).body
  const create = async (title, schedule) => {
    const r = await api('sessions', 'POST', { title, mode: 'Scheduled', schedule, prompt: 'proof', agent: 'Claude', authMode: 'ApiKey', projectId: source.id })
    assert.equal(r.status, 200, JSON.stringify(r.body)); assert.equal(r.body.failureReason, null, r.body.failureReason); return r.body.id
  }
  const every = await create('Every minute ' + n, '* * * * *')
  const yearly = await create('Yearly ' + n, '0 0 1 1 *')
  log('created', every, yearly)

  // 1. A real run in the source project. No kubelet: the pod stays Pending, i.e. the run is active.
  const firstPod = await until('first run pod', () => runPods(every)[0])
  const firstEnv = resolvedEnv(firstPod)
  assert.equal(firstEnv[oldEnv.name], OLD); assert.ok(!(newEnv.name in firstEnv))
  log('first run', firstPod.metadata.name, 'has source env')

  // 2. Ownership and refusals, against the real controllers.
  const move = (id, body, owner) => api('session-organization/' + id + '/project', 'POST', { confirm: true, ...body }, owner)
  assert.equal((await move(every, { projectId: foreign.id })).status, 404)
  assert.equal((await move(every, { projectId: target.id }, 'proof-other')).status, 404)
  assert.equal((await api('sessions/' + every, 'PATCH', { projectId: target.id })).status, 409)
  const refused = await move(every, { projectId: target.id })
  assert.equal(refused.status, 409); assert.equal(refused.body.code, 'scheduled_run_active')
  let c = cron(every)
  assert.ok(!c.spec.suspend); assert.ok(!Object.keys(c.metadata.annotations || {}).some(k => k.startsWith('agenthub.dev/project-move')))
  assert.equal((await api('sessions/' + every)).body.projectId, source.id)
  assert.equal(runPods(every)[0].metadata.name, firstPod.metadata.name)
  log('404 foreign project/owner, 409 PATCH, 409 scheduled_run_active; schedule restored, run untouched')

  // 3. Keyboard: Organize -> target -> Stop run and move.
  await page.reload()
  const row = page.locator('[data-session-row="' + every + '"]'); await row.waitFor()
  await row.locator('[aria-label="Organize session"]').focus(); await page.keyboard.press('Enter')
  await page.getByLabel('Move to project').selectOption(target.id)
  const impact = await page.locator('[data-project-move-impact]').textContent()
  assert.match(impact, /2 removed, 2 added/); assert.match(impact, /A run is in progress/)
  const button = page.getByRole('button', { name: 'Stop run and move', exact: true })
  await button.focus(); const movedAt = Date.now(); await page.keyboard.press('Enter')
  await page.getByRole('dialog').waitFor({ state: 'hidden', timeout: 150000 })
  const moved = (await api('sessions/' + every)).body
  assert.equal(moved.projectId, target.id); assert.equal(moved.scheduleMoveUnfinished, false)
  assert.equal(runJobs(every).filter(j => j.metadata.name === firstPod.metadata.labels['job-name']).length, 0)
  assert.ok(!runPods(every).some(p => p.metadata.name === firstPod.metadata.name))
  c = cron(every)
  assert.ok(!c.spec.suspend); assert.ok(!Object.keys(c.metadata.annotations || {}).some(k => k.startsWith('agenthub.dev/project-move')))
  const names = agentEnv(c.spec.jobTemplate.spec.template.spec).map(e => e.name)
  assert.ok(names.includes(newEnv.name)); assert.ok(!names.includes(oldEnv.name))
  const envCopy = decode(get('secret', 'sessionenv-' + every)), fileCopy = decode(get('secret', 'sessionsecrets-' + every))
  assert.deepEqual(Object.values(envCopy), [NEW]); assert.deepEqual(Object.values(fileCopy), [NEW + '-file'])
  log('Stop run and move: run gone, template + copies hold the destination only, schedule active')

  // 4. The next real scheduled run.
  const nextPod = await until('next run pod', () => runPods(every).find(p => Date.parse(p.metadata.creationTimestamp) >= movedAt - 1000))
  const env = resolvedEnv(nextPod)
  assert.equal(env[newEnv.name], NEW); assert.ok(!(oldEnv.name in env))
  assert.ok(!Object.values(env).some(v => String(v).includes(OLD)))
  assert.equal(nextPod.spec.volumes.find(v => v.name === 'session-secrets').secret.secretName, 'sessionsecrets-' + every)
  log('next run', nextPod.metadata.name, 'env', JSON.stringify({ [newEnv.name]: env[newEnv.name], AGENTHUB_SECRET_ENV: env.AGENTHUB_SECRET_ENV }))

  // 5. Touch at phone width: an idle schedule moves with Confirm move.
  const touch = await browser.newContext({ viewport: { width: 390, height: 844 }, hasTouch: true, isMobile: true })
  const phone = await touch.newPage(); phone.on('pageerror', e => errors.push(e.message))
  await phone.goto(process.env.PROOF_URL)
  // As in browser.cjs: at phone width a tap opens the session, and the Sessions button brings the list back.
  const yearlyRow = phone.locator('[data-session-row="' + yearly + '"]')
  await yearlyRow.tap()
  await phone.getByRole('button', { name: 'Sessions', exact: true }).tap()
  await yearlyRow.locator('[aria-label="Organize session"]').tap()
  await phone.getByLabel('Move to project').selectOption(target.id)
  await phone.getByRole('button', { name: 'Confirm move', exact: true }).tap()
  await phone.getByRole('dialog').waitFor({ state: 'hidden', timeout: 60000 })
  assert.equal((await api('sessions/' + yearly)).body.projectId, target.id)
  const yc = cron(yearly), ynames = agentEnv(yc.spec.jobTemplate.spec.template.spec).map(e => e.name)
  assert.ok(!yc.spec.suspend); assert.ok(ynames.includes(newEnv.name)); assert.ok(!ynames.includes(oldEnv.name))
  log('touch 390x844: idle schedule moved, template holds destination only')

  assert.deepEqual(errors, [])
  console.log('PASS real Chromium + real controllers/Postgres + real kube-apiserver with CronJob/Job controllers: 404 foreign, 409 PATCH, 409 scheduled_run_active with schedule restored, keyboard Stop run and move, next scheduled run references destination env only, touch move of idle schedule')
  await touch.close(); await context.close(); await browser.close()
})().catch(e => { console.error(e); process.exit(1) })
