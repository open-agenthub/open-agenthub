// #301: a scheduled session's template follows changes of its project's secret selection, a
// rename, and the owner's grants. The real CronJob and Job controllers start the runs; each run's
// pod must resolve exactly the current selection. A ValidatingAdmissionPolicy that refuses
// CronJob updates makes the template patch fail for real, to show the outdated state and the
// owner's Update schedule retry.
const { chromium } = require(process.env.PLAYWRIGHT_PATH || '/opt/session-agent/node_modules/playwright-core')
const { execFileSync } = require('node:child_process')
const assert = require('node:assert/strict')
const kube = (args, input) => execFileSync(process.env.KUBECTL, ['--kubeconfig', process.env.PROOF_KUBECONFIG, ...args], { encoding: 'utf8', input })
const kubectl = (...args) => kube(['-n', 'agenthub-sessions', ...args])
const get = (...args) => JSON.parse(kubectl('get', ...args, '-o', 'json'))
const sleep = ms => new Promise(r => setTimeout(r, ms))
const log = (...a) => console.log(new Date().toISOString(), ...a)
async function until(what, check, ms = 150000) {
  const end = Date.now() + ms
  for (;;) { const v = await check(); if (v) return v; if (Date.now() > end) throw new Error('timed out: ' + what); await sleep(2000) }
}
const decode = secret => Object.fromEntries(Object.entries(secret.data || {}).map(([k, v]) => [k, Buffer.from(v, 'base64').toString()]))
const runPods = id => get('pod', '-l', 'agenthub.dev/session=' + id + ',agenthub.dev/component=agent').items
const templateNames = id => get('cronjob', 'session-' + id).spec.jobTemplate.spec.template.spec.containers
  .find(c => c.name === 'agent').env.find(e => e.name === 'AGENTHUB_SECRET_ENV').value.split(',').filter(Boolean).sort()
// The session-secret variables a run's container would receive: each reference into the
// session's variable copy, resolved against the copy as it is now.
function secretEnv(pod) {
  const out = {}
  for (const e of pod.spec.containers.find(c => c.name === 'agent').env) {
    const ref = e.valueFrom?.secretKeyRef; if (!ref || !ref.name.startsWith('sessionenv-')) continue
    const data = decode(get('secret', ref.name)); if (ref.key in data) out[e.name] = data[ref.key]
  }
  return out
}
// Without a kubelet a run's pod stays Pending, and the CronJob's Forbid policy holds the next run
// back until it is gone: deleting the run stands in for it finishing.
async function nextRun(id, after) {
  for (const pod of runPods(id)) kubectl('delete', 'job', pod.metadata.labels['job-name'], '--wait=false')
  return until('run after ' + new Date(after).toISOString(), () => runPods(id).find(p => Date.parse(p.metadata.creationTimestamp) >= after - 1000))
}

;(async () => {
  const browser = await chromium.connectOverCDP(process.env.CDP_ENDPOINT)
  const context = await browser.newContext({ viewport: { width: 1440, height: 1000 } })
  const page = await context.newPage(); const errors = []; page.on('pageerror', e => errors.push(e.message))
  await page.goto(process.env.PROOF_URL)
  await page.getByRole('button', { name: /Give an agent a task/ }).first().waitFor()
  const api = (path, method = 'GET', body) => page.evaluate(async ({ path, method, body }) => {
    const r = await fetch('/api/' + path, { method, headers: { 'Content-Type': 'application/json' }, body: body ? JSON.stringify(body) : undefined })
    const text = await r.text(); let data; try { data = JSON.parse(text) } catch { data = text }
    return { status: r.status, body: data }
  }, { path, method, body })

  const n = Date.now()
  const secret = async (name, value) => (await api('secrets', 'POST', { name: name + '_' + n, delivery: 'env', value: value + '-' + n, description: 'proof' })).body
  const a = await secret('ALPHA', 'alpha'), b = await secret('BRAVO', 'bravo'), c = await secret('CHARLIE', 'charlie')
  const project = (await api('projects', 'POST', { name: 'Schedule secrets ' + n, color: null, secretIds: [a.id] })).body
  const created = await api('sessions', 'POST', { title: 'Every minute ' + n, mode: 'Scheduled', schedule: '* * * * *', prompt: 'proof',
    agent: 'Claude', authMode: 'ApiKey', projectId: project.id })
  assert.equal(created.status, 200, JSON.stringify(created.body)); assert.equal(created.body.failureReason, null)
  const id = created.body.id
  const first = await until('first run', () => runPods(id)[0])
  assert.deepEqual(secretEnv(first), { [a.name]: 'alpha-' + n })
  log('run 1', first.metadata.name, JSON.stringify(secretEnv(first)))

  // 1. The project's selection gains BRAVO, then ALPHA is renamed, while run 1 is in progress.
  assert.equal((await api('projects/' + project.id, 'PATCH', { secretIds: [a.id, b.id] })).status, 200)
  const renamed = 'ALPHA_RENAMED_' + n
  assert.equal((await api('secrets', 'POST', { id: a.id, name: renamed, delivery: 'env', value: 'alpha-' + n, description: 'proof' })).status, 200)
  assert.deepEqual(templateNames(id), [renamed, b.name].sort())
  assert.ok(runPods(id).some(p => p.metadata.name === first.metadata.name), 'run 1 keeps its pod')

  // 2. The owner grants CHARLIE in the session view, a control scheduled sessions did not have.
  await page.goto(process.env.PROOF_URL + '/s/' + id)
  await page.locator('[data-session-secrets] summary').click()
  assert.match(await page.locator('[data-scheduled-grants-note]').textContent(), /next scheduled run/)
  const granted = page.waitForResponse(r => r.url().endsWith('/api/sessions/' + id) && r.request().method() === 'PATCH')
  await page.locator('[data-secret-grant="' + c.id + '"] [data-grant-level="session"]').click()
  assert.equal((await granted).status(), 200)
  assert.deepEqual(templateNames(id), [renamed, b.name, c.name].sort())

  const second = await nextRun(id, Date.now())
  assert.deepEqual(secretEnv(second), { [renamed]: 'alpha-' + n, [b.name]: 'bravo-' + n, [c.name]: 'charlie-' + n })
  log('run 2', second.metadata.name, JSON.stringify(secretEnv(second)))

  // 3. A real refusal of the template patch: the change happens, the schedule says it is behind.
  kube(['apply', '-f', '-'], JSON.stringify({ apiVersion: 'v1', kind: 'List', items: [
    { apiVersion: 'admissionregistration.k8s.io/v1', kind: 'ValidatingAdmissionPolicy', metadata: { name: 'proof-freeze-schedules' },
      spec: { failurePolicy: 'Fail', matchConstraints: { resourceRules: [{ apiGroups: ['batch'], apiVersions: ['v1'], operations: ['UPDATE'], resources: ['cronjobs'] }] },
        validations: [{ expression: 'false', message: 'proof: schedules are frozen' }] } },
    { apiVersion: 'admissionregistration.k8s.io/v1', kind: 'ValidatingAdmissionPolicyBinding', metadata: { name: 'proof-freeze-schedules' },
      spec: { policyName: 'proof-freeze-schedules', validationActions: ['Deny'] } }] }))
  await until('policy in force', () => { try { kubectl('annotate', 'cronjob', 'session-' + id, 'proof/probe=' + Date.now(), '--overwrite'); return false } catch { return true } }, 30000)
  const removed = await api('projects/' + project.id, 'PATCH', { secretIds: [b.id] })
  assert.equal(removed.status, 200, 'the project change is not failed over the schedule')
  assert.deepEqual(templateNames(id), [renamed, b.name, c.name].sort())
  assert.equal((await api('sessions/' + id)).body.scheduleSecretsOutdated, true)
  await page.reload()
  await page.locator('[data-session-row="' + id + '"] [data-schedule-secrets-outdated]').waitFor()
  const note = page.locator('[data-schedule-secrets-note="' + id + '"]')
  assert.match(await note.textContent(), /previous secret variables/)
  const refused = page.waitForResponse(r => r.url().endsWith('/apply-resources'))
  await note.locator('[data-schedule-secrets-update]').click()
  assert.equal((await refused).status(), 409)
  await note.locator('[role="alert"]').waitFor()
  log('policy refuses the patch: project change saved, scheduleSecretsOutdated, sidebar marker, Update schedule refused with', (await note.locator('[role="alert"]').textContent()).slice(0, 80))

  // 4. Fail closed: a run that starts from the outdated template after a revocation (ALPHA left
  // the project) and a deletion (CHARLIE). Its pod still names both, but the copy was rewritten
  // first, so neither value is there; the references are optional, so the missing keys leave the
  // variables unset instead of failing the container.
  assert.equal((await api('secrets/' + c.id, 'DELETE')).status, 204)
  assert.deepEqual(templateNames(id), [renamed, b.name, c.name].sort())
  const during = await nextRun(id, Date.now())
  const refs = during.spec.containers.find(x => x.name === 'agent').env.filter(e => e.valueFrom?.secretKeyRef?.name === 'sessionenv-' + id)
  assert.deepEqual(refs.map(e => e.name).sort(), [renamed, b.name, c.name].sort())
  assert.ok(refs.every(e => e.valueFrom.secretKeyRef.optional === true))
  assert.deepEqual(Object.keys(decode(get('secret', 'sessionenv-' + id))), [b.name])
  assert.deepEqual(secretEnv(during), { [b.name]: 'bravo-' + n })
  log('run during refusal', during.metadata.name, 'names', refs.map(e => e.name).join(','), 'resolves', JSON.stringify(secretEnv(during)), 'all optional')

  kube(['delete', 'validatingadmissionpolicybinding,validatingadmissionpolicy', 'proof-freeze-schedules'])
  await until('policy lifted', () => { try { kubectl('annotate', 'cronjob', 'session-' + id, 'proof/probe-', '--overwrite'); return true } catch { return false } }, 30000)
  const applied = page.waitForResponse(r => r.url().endsWith('/apply-resources'))
  await note.locator('[data-schedule-secrets-update]').click()
  assert.equal((await applied).status(), 200)
  await note.waitFor({ state: 'detached' })
  assert.equal((await api('sessions/' + id)).body.scheduleSecretsOutdated, false)
  assert.deepEqual(templateNames(id), [b.name])

  const third = await nextRun(id, Date.now())
  assert.deepEqual(secretEnv(third), { [b.name]: 'bravo-' + n })
  log('run 3', third.metadata.name, JSON.stringify(secretEnv(third)))

  assert.deepEqual(errors, [])
  console.log('PASS real Chromium + real controllers/Postgres + real kube-apiserver with CronJob/Job controllers: project selection, rename and session-view grant reach the next run exactly; a run started from the refused, outdated template after a revocation and a deletion gets neither value (optional references, copy rewritten first); a real admission refusal leaves the change saved, reports scheduleSecretsOutdated with a sidebar marker, and Update schedule applies it once allowed')
  await context.close(); await browser.close()
})().catch(e => { console.error(e); process.exit(1) })
