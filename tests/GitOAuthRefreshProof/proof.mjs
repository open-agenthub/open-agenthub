// #289 proof against a running backend (backend.mjs), the kubelet stand-in and the mock GitLab.
// Run with NODE_EXTRA_CA_CERTS=<rig>/mock/certs/ca.crt:
//   node proof.mjs expiry <rig> <admin-kubeconfig> [seconds]   one session, git over HTTPS through
//       the pod's credential store for several token lifetimes; then the owner revokes the app,
//       and signs in again.
//   node proof.mjs concurrency <rig> <admin-kubeconfig>         the token is due and many callers
//       want it at once: session starts and project searches, on one backend.
// Prints no token: outputs are scrubbed, and at the end every token the rig ever saw is searched
// for in every log.
import { execFileSync, spawnSync } from 'node:child_process'
import fs from 'node:fs'
import path from 'node:path'

const [phase, rig, kc, secondsArg] = process.argv.slice(2)
const API = 'http://127.0.0.1:8080/api'
const MOCK = 'https://127.0.0.10'
const REPO = `${MOCK}/group/project.git`
const k = (...a) => execFileSync(process.env.KUBECTL || 'kubectl', ['--kubeconfig', kc, '-n', 'agenthub-sessions', ...a], { encoding: 'utf8' })
const sleep = ms => new Promise(r => setTimeout(r, ms))
const t0 = Date.now()
const at = () => `t+${String(Math.round((Date.now() - t0) / 1000)).padStart(3)}s`
const seenTokens = new Set()
const scrub = s => String(s).replace(/[0-9a-f]{48}/g, '<token>')
const say = m => console.log(`${at()} ${scrub(m)}`)
const results = []
const check = (ok, what) => { results.push(ok); console.log(`${ok ? 'PASS' : 'FAIL'} ${scrub(what)}`) }

async function api(method, p, body) {
  const r = await fetch(API + p, { method, headers: { 'Content-Type': 'application/json' }, body: body && JSON.stringify(body), redirect: 'manual' })
  const text = await r.text()
  let json = null
  try { json = JSON.parse(text) } catch { }
  return { status: r.status, json, text }
}
const stats = async () => (await fetch(MOCK + '/mock/stats')).json()

// The OAuth round trip a browser makes: hub → provider authorize → hub callback.
async function connect() {
  const { json } = await api('GET', '/git/connect/gitlab')
  const authorize = await fetch(json.url, { redirect: 'manual' })
  const callback = authorize.headers.get('location')
  const done = await fetch(callback, { redirect: 'manual' })
  return done.headers.get('location')
}

function remember() {
  // Collects the tokens currently stored, only to search the logs for them at the end.
  for (const name of JSON.parse(k('get', 'secrets', '-o', 'json')).items.map(s => s.metadata.name)) {
    if (!/^(gitauth|gitcreds)-/.test(name)) continue
    const data = JSON.parse(k('get', 'secret', name, '-o', 'json')).data || {}
    for (const v of Object.values(data)) for (const m of Buffer.from(v, 'base64').toString().matchAll(/[0-9a-f]{48}/g)) seenTokens.add(m[0])
  }
}

async function createSession(title) {
  const r = await api('POST', '/sessions', {
    mode: 'Interactive', agent: 'Claude', authMode: 'ApiKey', title,
    repos: [{ url: REPO, branch: 'main', providerId: 'gitlab' }]
  })
  if (r.status >= 300) throw new Error(`create ${title}: ${r.status} ${r.text}`)
  return r.json
}

async function home(id) {
  for (let i = 0; i < 120; i++) {
    const ipFile = path.join(rig, 'pods', id, 'ip')
    if (fs.existsSync(ipFile)) {
      const pod = JSON.parse(k('get', 'pod', `session-${id}`, '-o', 'json'))
      if (pod.status?.phase === 'Running') return path.join(rig, 'pods', id, 'home')
    }
    await sleep(500)
  }
  throw new Error(`session ${id} never ran`)
}

function git(homeDir, args, cwd = homeDir) {
  const r = spawnSync('git', args, { cwd, encoding: 'utf8', env: { ...process.env, HOME: homeDir, GIT_TERMINAL_PROMPT: '0', GIT_ASKPASS: '' } })
  const lastLine = (r.stderr || '').trim().split('\n').filter(Boolean).pop() || ''
  return { ok: r.status === 0, line: lastLine }
}

const storeHasLine = homeDir => {
  const f = path.join(homeDir, '.git-credentials')
  return fs.existsSync(f) && fs.readFileSync(f, 'utf8').includes('@127.0.0.10')
}

async function expiry() {
  const seconds = Number(secondsArg || 200)
  say(`connect: ${await connect()}`)
  remember()
  const s = await createSession('expiry')
  const h = await home(s.id)
  say(`session ${s.id} running; store has a line for the mock: ${storeHasLine(h)}`)
  const clone = git(h, ['clone', '-q', REPO, 'work'])
  check(clone.ok, `clone through the pod's credential store ${clone.ok ? '' : '— ' + clone.line}`)
  const work = path.join(h, 'work')
  const branch = `proof-${s.id}`
  git(h, ['checkout', '-q', '-b', branch], work)

  let ok = 0, failed = 0, firstFailure = null, n = 0, consecutive = 0, worstRun = 0
  const end = Date.now() + seconds * 1000
  while (Date.now() < end) {
    n++
    fs.writeFileSync(path.join(work, 'tick.txt'), `${n}\n`)
    git(h, ['add', '-A'], work)
    git(h, ['commit', '-q', '-m', `tick ${n}`], work)
    const fetchR = git(h, ['fetch', '-q', 'origin'], work)
    const push = git(h, ['push', '-q', 'origin', branch], work)
    if (fetchR.ok && push.ok) { ok++; consecutive = 0 }
    else { failed++; worstRun = Math.max(worstRun, ++consecutive); firstFailure ??= at(); say(`fetch ${fetchR.ok ? 'ok' : 'FAILED: ' + fetchR.line}; push ${push.ok ? 'ok' : 'FAILED: ' + push.line}; store has line: ${storeHasLine(h)}`) }
    remember()
    if (n % 6 === 0) say(`${ok} ok / ${failed} failed so far; mock: ${JSON.stringify(await stats())}`)
    await sleep(10_000)
  }
  const st = await stats()
  say(`after ${seconds}s: ${ok} fetch+push rounds ok, ${failed} failed (first failure ${firstFailure ?? 'never'}); mock: ${JSON.stringify(st)}`)
  // The mock revokes the previous access token the moment it renews (the strictest reading of
  // GitLab), so a git command whose requests straddle that moment fails once; the next one has the
  // new token. What must never happen is the main-branch failure: every command failing from the
  // first expiry on.
  check(ok > 0 && worstRun <= 1 && failed <= st.refreshes,
    `git fetch+push kept working for ${seconds}s across ${st.refreshes} renewals (${failed} single in-flight failures, never two in a row)`)
  check(st.invalidGrants === 0, `no refresh token was spent twice (invalid_grant: ${st.invalidGrants})`)

  if (process.env.SKIP_REVOKE) return s
  // The owner revokes the application at the provider: the next renewal is refused.
  await fetch(MOCK + '/mock/revoke', { method: 'POST' })
  say('revoked the application at the mock')
  let providers
  for (let i = 0; i < 60; i++) {
    providers = (await api('GET', '/git/providers')).json
    if (providers[0]?.needsSignIn) break
    await sleep(1000)
  }
  check(providers[0]?.needsSignIn === true, `GET /api/git/providers says needsSignIn: ${JSON.stringify(providers[0])}`)
  await sleep(2000)
  check(!storeHasLine(h), 'the refused token was taken out of the running pod\'s store')
  const dead = git(h, ['fetch', '-q', 'origin'], work)
  say(`fetch after the refusal: ${dead.ok ? 'ok' : dead.line}`)
  check(!dead.ok, 'a fetch now fails (no token), instead of retrying a dead one against the provider')

  say(`sign in again: ${await connect()}`)
  remember()
  await sleep(3000)
  const back = git(h, ['push', '-q', 'origin', branch], work)
  check(storeHasLine(h) && back.ok, `after signing in again the SAME pod pushes without a restart ${back.ok ? '' : '— ' + back.line}`)
  check(!(await api('GET', '/git/providers')).json[0].needsSignIn, 'needsSignIn is cleared')
  return s
}

async function concurrency() {
  say(`connect: ${await connect()}`)
  remember()
  // Wait until the token is due for renewal (expires within the margin), with no background pass
  // running (AgentHub__GitOAuthRefresh__Enabled=false on this backend), so the callers below are
  // the only ones who could renew it.
  const wait = Number(process.env.WAIT_DUE || 40)
  say(`waiting ${wait}s until the stored token is due`)
  await sleep(wait * 1000)
  const before = await stats()
  const storedAccess = () => { const n = JSON.parse(k('get', 'secrets', '-o', 'json')).items.find(x => x.metadata.name.startsWith('gitauth-')); return Buffer.from(n.data['gitlab.access'], 'base64').toString() }
  const accessBefore = storedAccess()
  const calls = [
    ...Array.from({ length: 3 }, (_, i) => api('POST', '/sessions', {
      mode: 'Interactive', agent: 'Claude', authMode: 'ApiKey', title: `race-${i}`,
      repos: [{ url: REPO, branch: 'main', providerId: 'gitlab' }]
    })),
    ...Array.from({ length: 6 }, () => api('GET', '/git/projects?provider=gitlab'))
  ]
  const answers = await Promise.all(calls)
  const after = await stats()
  remember()
  say(`answers: ${answers.map(a => a.status).join(' ')}`)
  say(`mock during the burst: refreshes ${after.refreshes - before.refreshes}, invalid_grant ${after.invalidGrants - before.invalidGrants}`)
  check(answers.every(a => a.status < 300), 'every concurrent call succeeded')
  check(after.refreshes - before.refreshes === 1, `the grant was renewed exactly once (${after.refreshes - before.refreshes})`)
  check(after.invalidGrants === before.invalidGrants, 'no caller redeemed a spent refresh token')
  const projects = answers.slice(3).map(a => Array.isArray(a.json) ? a.json.length : -1)
  check(projects.every(n => n === 1), `every project search got the project (${projects.join(',')})`)
  // Each session's store holds the one renewed token.
  const homes = await Promise.all(answers.slice(0, 3).map(a => home(a.json.id)))
  const lines = homes.map(h => fs.existsSync(path.join(h, '.git-credentials')) ? fs.readFileSync(path.join(h, '.git-credentials'), 'utf8') : '')
  check(new Set(lines).size === 1 && lines[0].includes('@127.0.0.10') && !lines[0].includes(accessBefore) && lines[0].includes(storedAccess()),
    'all three sessions got the same token, the renewed one (not the one the renewal revoked)')
  for (const h of homes) {
    const r = git(h, ['ls-remote', REPO])
    check(r.ok, `ls-remote from ${path.basename(path.dirname(h))} ${r.ok ? '' : '— ' + r.line}`)
  }
}

try {
  if (phase === 'expiry') await expiry()
  else if (phase === 'concurrency') await concurrency()
  else throw new Error('phase: expiry | concurrency')
} catch (e) {
  check(false, 'run aborted: ' + e.message)
}

// Security: no token the rig ever stored may appear in a log.
const logs = []
const walk = d => { for (const f of fs.readdirSync(d, { withFileTypes: true })) { const p = path.join(d, f.name); if (f.isDirectory()) { if (f.name !== 'home' && f.name !== 'repos') walk(p) } else if (/\.log$/.test(f.name)) logs.push(p) } }
walk(rig)
const leaks = logs.filter(f => { const t = fs.readFileSync(f, 'utf8'); return [...seenTokens].some(tok => t.includes(tok)) })
check(seenTokens.size > 0 && leaks.length === 0, `none of the ${seenTokens.size} tokens appears in ${logs.length} logs (${logs.map(l => path.relative(rig, l)).join(', ')})${leaks.length ? ' LEAKS: ' + leaks.join(', ') : ''}`)
console.log(`${results.filter(Boolean).length}/${results.length} checks passed`)
process.exit(results.every(Boolean) ? 0 : 1)
