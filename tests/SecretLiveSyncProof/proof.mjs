// #255 proof: secret changes reach a RUNNING session container. Files through the kubelet's
// projection of the whole-secret mount; variables at the next start, reported meanwhile as
// secretsPendingRestart. Everything is real except the agent process:
//   - the branch's backend (dev auth) acting as the chart's agenthub-backend service account,
//   - k3s v1.34.1 in a QEMU guest, i.e. a real kube-apiserver AND a real kubelet/containerd,
//   - real Postgres,
//   - the agent container runs memcached:alpine (uid 1000, read-only root filesystem) in place of
//     the agent runtime, so `kubectl exec` can read ~/.secrets and the process environment. What a
//     file or a variable looks like inside the container is the kubelet's doing, not the runtime's.
// README.md says how to bring the rig up. Run: node proof.mjs <admin-kubeconfig> [postgres-url]
import { execFileSync } from 'node:child_process'
import { createRequire } from 'node:module'

const [kubeconfig, pgUrl = 'postgres://postgres@127.0.0.1:54329/agenthub255'] = process.argv.slice(2)
const pg = createRequire(import.meta.url)('pg')
const NS = 'agenthub-sessions'
const API = 'http://127.0.0.1:8080/api'
const kubectl = (...args) => execFileSync(process.env.KUBECTL || 'kubectl', ['--kubeconfig', kubeconfig, '-n', NS, ...args],
  { encoding: 'utf8', stdio: ['ignore', 'pipe', 'ignore'] })
async function api(method, p, body, headers = {}) {
  const r = await fetch(API.replace(/\/api$/, '') + (p.startsWith('/internal') ? p : '/api' + p),
    { method, headers: { 'Content-Type': 'application/json', ...headers }, body: body && JSON.stringify(body) })
  const text = await r.text()
  let json = null
  try { json = JSON.parse(text) } catch { }
  return { status: r.status, json, text }
}
const sleep = ms => new Promise(r => setTimeout(r, ms))
const results = []
const check = (ok, what, detail = '') => { results.push(ok); console.log(`${ok ? 'PASS' : 'FAIL'} ${what}${detail ? ' — ' + detail : ''}`) }

let podName
const sh = script => kubectl('exec', podName, '-c', 'agent', '--', 'sh', '-c', script)
const file = name => sh(`cat "$HOME/.secrets/${name}" 2>/dev/null || echo '<absent>'`).trim()
const files = () => sh('ls "$HOME/.secrets"').split('\n').filter(Boolean).sort().join(',')
// The environment of PID 1, the container's own process, not of the exec'd shell — the latter is
// built from the same spec, but PID 1 is what an agent started at container start would have.
const env = name => (sh('tr "\\0" "\\n" < /proc/1/environ').split('\n').find(l => l.startsWith(name + '=')) ?? `${name}=<unset>`).slice(name.length + 1)
/** Polls until the file in the pod reads `want`; returns seconds waited, or null after the limit. */
async function fileBecomes(name, want, limit = 180) {
  const start = Date.now()
  while (Date.now() - start < limit * 1000) {
    if (file(name) === want) return Math.round((Date.now() - start) / 1000)
    await sleep(2000)
  }
  return null
}
async function runningPod(id, notUid) {
  for (let i = 0; i < 120; i++) {
    try {
      const pod = JSON.parse(kubectl('get', 'pod', `session-${id}`, '-o', 'json'))
      if (pod.status?.phase === 'Running' && pod.metadata.uid !== notUid && !pod.metadata.deletionTimestamp) return pod
    } catch { }
    await sleep(2000)
  }
  throw new Error(`session-${id} is not running`)
}
/** Pause, wait for the old pod to be gone, resume: what the web app's restart does. Resuming while
 * the old pod still terminates is refused by the API server (the name is taken). */
async function restart(id) {
  await api('POST', `/sessions/${id}/pause`)
  for (let i = 0; i < 90; i++) {
    try { kubectl('get', 'pod', `session-${id}`, '-o', 'name') } catch { break }
    await sleep(2000)
  }
  const resumed = await api('POST', `/sessions/${id}/resume`)
  if (resumed.status !== 200) throw new Error(`resume: ${resumed.status} ${resumed.text}`)
}
const pending = async id => (await api('GET', `/sessions/${id}`)).json.secretsPendingRestart
const pendingInList = async id => (await api('GET', '/sessions')).json.find(s => s.id === id).secretsPendingRestart
const put = async body => (await api('POST', '/secrets', body)).json

// Setup: a project selecting a file and a variable, a session in it, running under the kubelet.
const f1 = await put({ name: 'token.txt', delivery: 'file', value: 'file-v1-000001' })
const e1 = await put({ name: 'DEPLOY_TOKEN', delivery: 'env', value: 'env-v1-0000001' })
const project = (await api('POST', '/projects', { name: 'Live sync', secretIds: [f1.id, e1.id] })).json
const session = (await api('POST', '/sessions', { mode: 'Interactive', agent: 'Claude', authMode: 'ApiKey', title: 'live-sync', projectId: project.id })).json
let pod = await runningPod(session.id)
podName = pod.metadata.name
check(file('token.txt') === 'file-v1-000001' && env('DEPLOY_TOKEN') === 'env-v1-0000001',
  'the running container has the file and the variable')
check(await pending(session.id) === false, 'nothing pending right after the start',
  `annotation secret-env-version=${pod.metadata.annotations['agenthub.dev/secret-env-version']}`)

// 1. Created and granted after the session started.
const f2 = await put({ name: 'granted.json', delivery: 'file', value: '{"granted":"after-start-01"}' })
const e2 = await put({ name: 'GRANTED_VAR', delivery: 'env', value: 'granted-env-0001' })
let r = await api('PATCH', `/sessions/${session.id}`, { secretGrants: [{ secretId: f2.id }, { secretId: e2.id }] })
check(r.status === 200 && r.json.secretsPendingRestart === true, 'granting a variable to the running session answers secretsPendingRestart')
let took = await fileBecomes('granted.json', '{"granted":"after-start-01"}')
check(took !== null, 'a file granted after the start appears in the running container', `after ${took}s`)
check(env('GRANTED_VAR') === '<unset>', 'a variable granted after the start is not in the running process (as documented)')
check(await pendingInList(session.id) === true, 'the session listing reports it too')

// 2. Granted by answering the agent's own request: restartRequired tells the owner.
const db = new pg.Client({ connectionString: pgUrl })
await db.connect()
const callback = (await db.query('select callback_token from sessions where id = $1', [session.id])).rows[0].callback_token
await db.end()
const ask = async (name, delivery) => (await api('POST', `/internal/sessions/${session.id}/secret-requests`,
  { name, delivery, purpose: 'proof' }, { 'X-Agent-Token': callback })).json.id
const e3 = await put({ name: 'REQ_VAR', delivery: 'env', value: 'requested-env-01' })
r = await api('POST', `/secret-requests/${await ask('REQ_VAR', 'env')}/provide`, { secretId: e3.id })
check(r.status === 200 && r.json.restartRequired === true, 'answering a request with a variable says a restart is required', JSON.stringify(r.json))
const f3 = await put({ name: 'req.txt', delivery: 'file', value: 'requested-file-1' })
r = await api('POST', `/secret-requests/${await ask('req.txt', 'file')}/provide`, { secretId: f3.id })
check(r.status === 200 && r.json.restartRequired === false, 'answering with a file needs no restart')
took = await fileBecomes('req.txt', 'requested-file-1')
check(took !== null, 'and the file appears', `after ${took}s`)

// 3. A new value.
await put({ id: f1.id, name: 'token.txt', delivery: 'file', value: 'file-v2-000002' })
took = await fileBecomes('token.txt', 'file-v2-000002')
check(took !== null, 'a changed file value reaches the running container', `after ${took}s`)
await put({ id: e1.id, name: 'DEPLOY_TOKEN', delivery: 'env', value: 'env-v2-0000002' })
check(env('DEPLOY_TOKEN') === 'env-v1-0000001', 'a changed variable keeps its old value in the running process')

// 4. A rename: the old file goes, the new one comes, in the running container.
await put({ id: f1.id, name: 'token-renamed.txt', delivery: 'file' })
took = await fileBecomes('token-renamed.txt', 'file-v2-000002')
check(took !== null && file('token.txt') === '<absent>', 'a renamed file is replaced under its new name', `after ${took}s; files: ${files()}`)
await put({ id: e1.id, name: 'DEPLOY_TOKEN_V2', delivery: 'env' })
check(env('DEPLOY_TOKEN') === 'env-v1-0000001' && env('DEPLOY_TOKEN_V2') === '<unset>', 'a renamed variable is unchanged in the running process')
const copy = JSON.parse(kubectl('get', 'secret', `sessionenv-${session.id}`, '-o', 'json')).data
check(Object.keys(copy).sort().join(',') === 'DEPLOY_TOKEN_V2,GRANTED_VAR,REQ_VAR', 'the variable copy holds the new names only', Object.keys(copy).sort().join(','))

// 5. Applying is what restarts: the endpoint now restarts for variables, not only for resources.
r = await api('POST', `/sessions/${session.id}/apply-resources`)
check(r.status === 409 && r.text.includes('State storage is unavailable'),
  'apply-resources attempts the restart for pending variables (this rig has no object storage)', r.text)

// 6. Restart (pause, resume): the new container has the current variables, nothing pending.
await restart(session.id)
pod = await runningPod(session.id, pod.metadata.uid)
podName = pod.metadata.name
check(env('DEPLOY_TOKEN') === '<unset>' && env('DEPLOY_TOKEN_V2') === 'env-v2-0000002' && env('GRANTED_VAR') === 'granted-env-0001'
  && env('REQ_VAR') === 'requested-env-01', 'after a restart the process has the new value, the new name and the grants, not the old name')
check(await pending(session.id) === false, 'and nothing is pending')
r = await api('POST', `/sessions/${session.id}/apply-resources`)
check(r.status === 200, 'apply-resources without anything pending restarts nothing')

// 7. Writes that do not change a variable leave it alone: a file-only change, and saving a
// variable with the value it already has (the API server does not bump resourceVersion).
await put({ id: f1.id, name: 'token-renamed.txt', delivery: 'file', value: 'file-v3-000003' })
took = await fileBecomes('token-renamed.txt', 'file-v3-000003')
await put({ id: e1.id, name: 'DEPLOY_TOKEN_V2', delivery: 'env', value: 'env-v2-0000002' })
check(took !== null && await pending(session.id) === false, 'a file change or an unchanged variable is not a pending restart', `file after ${took}s`)

// 8. Revoke: the file disappears from the running container; the variable stays until a restart.
r = await api('PATCH', `/sessions/${session.id}`, { secretGrants: [] })
const start = Date.now()
while (file('granted.json') !== '<absent>' && Date.now() - start < 180_000) await sleep(2000)
check(file('granted.json') === '<absent>' && file('req.txt') === '<absent>', 'revoked files disappear from the running container',
  `after ${Math.round((Date.now() - start) / 1000)}s; files: ${files()}`)
check(env('GRANTED_VAR') === 'granted-env-0001' && r.json.secretsPendingRestart === true,
  'a revoked variable stays in the running process and is reported as pending (pause revokes at once)')

// 9. Narrowing the project and deleting a secret are changes of the same kind.
await api('PATCH', `/projects/${project.id}`, { secretIds: [f1.id] })
await restart(session.id)
pod = await runningPod(session.id, pod.metadata.uid)
podName = pod.metadata.name
check(env('DEPLOY_TOKEN_V2') === '<unset>' && env('GRANTED_VAR') === '<unset>' && file('token-renamed.txt') === 'file-v3-000003',
  'after the next start the narrowed and revoked variables are gone, the file stays')
await api('DELETE', `/secrets/${f1.id}`)
took = await fileBecomes('token-renamed.txt', '<absent>')
check(took !== null && files() === '', 'deleting a stored secret removes its file from the running container', `after ${took}s`)

console.log(`\n${results.filter(Boolean).length}/${results.length} passed`)
process.exit(results.every(Boolean) ? 0 : 1)
