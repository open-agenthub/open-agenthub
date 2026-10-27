// #303: deleting a secret fails closed. A ValidatingAdmissionPolicy makes the session copies'
// rewrite fail for real (phase A), then also their deletion (phase B). A reported success must
// mean no copy holds the value; a run started meanwhile must not receive it.
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
const copy = name => { try { return decode(get('secret', name)) } catch { return null } }
const runPods = id => get('pod', '-l', 'agenthub.dev/session=' + id + ',agenthub.dev/component=agent').items
function secretEnv(pod) {
  const out = {}
  for (const e of pod.spec.containers.find(c => c.name === 'agent').env) {
    const ref = e.valueFrom?.secretKeyRef; if (!ref || !ref.name.startsWith('sessionenv-')) continue
    const data = copy(ref.name); if (data && ref.key in data) out[e.name] = data[ref.key]
  }
  return out
}
async function nextRun(id, after) {
  for (const pod of runPods(id)) kubectl('delete', 'job', pod.metadata.labels['job-name'], '--wait=false')
  return until('run after ' + new Date(after).toISOString(), () => runPods(id).find(p => Date.parse(p.metadata.creationTimestamp) >= after - 1000))
}
// Refuses the given operations on the session copies only; the owner's store stays writable.
function refuseCopies(operations) {
  kube(['apply', '-f', '-'], JSON.stringify({ apiVersion: 'v1', kind: 'List', items: [
    { apiVersion: 'admissionregistration.k8s.io/v1', kind: 'ValidatingAdmissionPolicy', metadata: { name: 'proof-refuse-copies' },
      spec: { failurePolicy: 'Fail', matchConstraints: { resourceRules: [{ apiGroups: [''], apiVersions: ['v1'], operations, resources: ['secrets'] }] },
        matchConditions: [{ name: 'copies', expression: "(request.operation == 'DELETE' ? oldObject : object).metadata.name.startsWith('session')" }],
        validations: [{ expression: 'false', message: 'proof: session copies are frozen' }] } },
    { apiVersion: 'admissionregistration.k8s.io/v1', kind: 'ValidatingAdmissionPolicyBinding', metadata: { name: 'proof-refuse-copies' },
      spec: { policyName: 'proof-refuse-copies', validationActions: ['Deny'] } }] }))
}
const probe = n => ({ apiVersion: 'v1', kind: 'Secret', metadata: { name: 'session-probe-' + n, namespace: 'agenthub-sessions' } })
async function policyActive(active) {
  await until('policy ' + (active ? 'in force' : 'lifted'), () => {
    try { kube(['apply', '-f', '-'], JSON.stringify(probe(Date.now()))); return !active } catch { return active }
  }, 30000)
}
function liftPolicy() { kube(['delete', 'validatingadmissionpolicybinding,validatingadmissionpolicy', 'proof-refuse-copies']) }

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
  const keep = await secret('KEEP', 'keep'), doomed = await secret('DOOMED', 'doomed'), doomed2 = await secret('DOOMED_TWO', 'doomed-two')
  const project = (await api('projects', 'POST', { name: 'Delete proof ' + n, color: null, secretIds: [keep.id, doomed.id, doomed2.id] })).body
  const create = async (mode, extra) => {
    const r = await api('sessions', 'POST', { title: mode + ' ' + n, mode, prompt: 'proof', agent: 'Claude', authMode: 'ApiKey', projectId: project.id, ...extra })
    assert.equal(r.status, 200, JSON.stringify(r.body)); assert.equal(r.body.failureReason, null); return r.body.id
  }
  const scheduled = await create('Scheduled', { schedule: '* * * * *' })
  const interactive = await create('Interactive', {})
  const all = { [keep.name]: 'keep-' + n, [doomed.name]: 'doomed-' + n, [doomed2.name]: 'doomed-two-' + n }
  assert.deepEqual(secretEnv(await until('first run', () => runPods(scheduled)[0])), all)
  assert.deepEqual(copy('sessionenv-' + interactive), all)
  log('both sessions hold', Object.keys(all).join(','))

  // A. The copies cannot be rewritten: the delete withdraws them and is then truthfully done.
  refuseCopies(['CREATE', 'UPDATE']); await policyActive(true)
  assert.equal((await api('secrets/' + doomed.id, 'DELETE')).status, 204)
  for (const id of [scheduled, interactive]) {
    assert.equal(copy('sessionenv-' + id), null); assert.equal(copy('sessionsecrets-' + id), null)
  }
  const during = await nextRun(scheduled, Date.now())
  const refs = during.spec.containers.find(x => x.name === 'agent').env.filter(e => e.valueFrom?.secretKeyRef?.name === 'sessionenv-' + scheduled)
  assert.ok(refs.length > 0 && refs.every(e => e.valueFrom.secretKeyRef.optional === true))
  assert.ok(during.spec.volumes.find(v => v.name === 'session-secrets').secret.optional)
  assert.deepEqual(secretEnv(during), {})
  log('A: delete 204 with copies withdrawn; run', during.metadata.name, 'resolves', JSON.stringify(secretEnv(during)), '(optional refs, no copy)')

  liftPolicy(); await policyActive(false)
  // Any later sync brings the copies back, without the deleted value; the scheduled one by record.
  assert.equal((await api('projects/' + project.id, 'PATCH', { secretIds: [keep.id, doomed2.id] })).status, 200)
  const back = { [keep.name]: 'keep-' + n, [doomed2.name]: 'doomed-two-' + n }
  assert.deepEqual(copy('sessionenv-' + scheduled), back); assert.deepEqual(copy('sessionenv-' + interactive), back)
  log('A: copies restored by the next sync without', doomed.name)

  // B. The copies can be neither rewritten nor withdrawn: the delete is not reported as done.
  refuseCopies(['CREATE', 'UPDATE', 'DELETE']); await policyActive(true)
  await page.goto(process.env.PROOF_URL + '/settings/secrets')
  const row = page.locator('[data-secret="' + doomed2.name + '"]'); await row.waitFor()
  const first = page.waitForResponse(r => r.url().endsWith('/api/secrets/' + doomed2.id) && r.request().method() === 'DELETE')
  await row.locator('[data-secret-remove]').click(); await row.locator('[data-secret-remove]').click()
  const refused = await first
  assert.equal(refused.status(), 503); assert.equal((await refused.json()).code, 'secret_copies_pending')
  const notice = page.locator('[data-secret-delete-pending]'); await notice.waitFor()
  assert.match(await notice.textContent(), /may still hold the previous secret values/)
  // The honest state behind the 503: the copies still hold it.
  assert.equal(copy('sessionenv-' + scheduled)[doomed2.name], 'doomed-two-' + n)
  log('B: Chromium delete → 503 secret_copies_pending, notice with Retry; copies still hold the value')

  liftPolicy(); await policyActive(false)
  const retried = page.waitForResponse(r => r.url().endsWith('/api/secrets/' + doomed2.id) && r.request().method() === 'DELETE')
  await notice.locator('[data-secret-delete-retry]').click()
  assert.equal((await retried).status(), 204)
  await notice.waitFor({ state: 'detached' })
  for (const id of [scheduled, interactive]) assert.deepEqual(copy('sessionenv-' + id), { [keep.name]: 'keep-' + n })
  const after = await nextRun(scheduled, Date.now())
  assert.deepEqual(secretEnv(after), { [keep.name]: 'keep-' + n })
  log('B: Retry → 204; copies hold only', keep.name, '; run', after.metadata.name, 'resolves', JSON.stringify(secretEnv(after)))

  for (const s of get('secret').items.filter(s => s.metadata.name.startsWith('session-probe-'))) kubectl('delete', 'secret', s.metadata.name)
  // The store holds at most 32 secrets; leave room for the next run of this script.
  assert.equal((await api('secrets/' + keep.id, 'DELETE')).status, 204)
  assert.deepEqual(errors, [])
  console.log('PASS real Chromium + real controllers/Postgres + real kube-apiserver with CronJob/Job controllers: a delete whose copies cannot be rewritten withdraws them (204, run resolves nothing); one whose copies can be neither rewritten nor withdrawn answers 503 secret_copies_pending with a Retry notice; Retry after the refusal ends clears the value, and the next run resolves only the remaining secret')
  await context.close(); await browser.close()
})().catch(e => { console.error(e); process.exit(1) })
