// #453 proof: take a Claude Code session off the cluster and hand it back, against the REAL backend
// (dev auth), a real kube-apiserver, real Postgres and real object storage, with the REAL pinned
// Claude Code reading the conversation on both sides (pod.mjs, mock-model.mjs).
//   A. stdio MCP: the real mcp/agenthub server under a real MCP SDK client, with a personal token.
//   B. link path (what the remote /mcp and the web app hand out): the exact bash command, run as is.
// Run: node proof.mjs <repo>     (README.md lists what has to be running first)
import { execFileSync, execSync } from 'node:child_process'
import fs from 'node:fs'
import { createRequire } from 'node:module'
import path from 'node:path'
import { claudeEnv, claudeHome } from './claude-env.mjs'

const [repo] = process.argv.slice(2)
const mcp = createRequire(path.join(repo, 'mcp/agenthub/package.json'))
const { Client } = mcp('@modelcontextprotocol/sdk/client/index.js')
const { StdioClientTransport } = mcp('@modelcontextprotocol/sdk/client/stdio.js')
const RIG = process.env.RIG || process.env.HOME + '/rig'
const MODEL = RIG + '/model'
const BASE = 'http://127.0.0.1:8080'
const INGRESS = 'http://127.0.0.1:8081' // access-proxy.mjs, FrontendOrigin
const sleep = ms => new Promise(r => setTimeout(r, ms))
const results = []
const check = (ok, what, detail = '') => { results.push(ok); console.log(`${ok ? 'PASS' : 'FAIL'} ${what}${detail ? ' — ' + detail : ''}`) }

async function api(method, p, body, token) {
  const headers = { 'Content-Type': 'application/json', ...(token ? { Authorization: `Bearer ${token}` } : {}) }
  const r = await fetch(BASE + p, { method, headers, body: body && JSON.stringify(body) })
  const text = await r.text()
  let json = null
  try { json = JSON.parse(text) } catch { }
  return { status: r.status, json, text }
}
const requests = () => fs.existsSync(MODEL) ? fs.readdirSync(MODEL).filter(f => f.startsWith('req-')).length : 0
const lastRequest = () => fs.readFileSync(`${MODEL}/req-${requests()}.json`, 'utf8')
async function podTurn(prompt, before) {
  fs.writeFileSync(RIG + '/next-prompt.txt', prompt)
  return before
}
async function waitForTurns(count, what) {
  for (let i = 0; i < 240 && requests() < count; i++) await sleep(500)
  if (requests() < count) throw new Error(`no model request for ${what}`)
  await sleep(3000) // the stand-in archives and uploads after the CLI exits
}
async function phase(id) { return (await api('GET', `/api/sessions/${id}`)).json?.phase }
function transcriptIn(archive, folder) {
  const dir = fs.mkdtempSync(RIG + '/inspect-')
  fs.writeFileSync(dir + '.tgz', archive)
  execFileSync('tar', ['xzf', dir + '.tgz', '-C', dir])
  const projects = path.join(dir, '.claude/projects', folder)
  const file = fs.readdirSync(projects).find(f => f.endsWith('.jsonl'))
  return fs.readFileSync(path.join(projects, file), 'utf8')
}
function localClaude(home, cwd, prompt, id) {
  return execFileSync('claude', ['-p', prompt, '--resume', id], { cwd, env: claudeEnv(home), encoding: 'utf8', timeout: 120000 }).trim()
}

// ---------------------------------------------------------------- setup: a session that has run
const token = (await api('POST', '/api/tokens', { name: 'proof-453' })).json.token
await podTurn('Remember the code word PINEAPPLE-7. Reply with ok.')
const session = (await api('POST', '/api/sessions', { title: 'transfer', mode: 'Interactive', agent: 'Claude', authMode: 'ApiKey' })).json
const id = session.id
await waitForTurns(1, 'the pod\'s first turn')
check((await phase(id)) === 'Running', 'session ran a real Claude Code turn in its pod and is Running')

// ---------------------------------------------------------------- A. stdio MCP server
const laptop = RIG + '/laptop-a', project = laptop + '/work/my.project'
fs.rmSync(laptop, { recursive: true, force: true })
fs.mkdirSync(project, { recursive: true })
claudeHome(laptop, [project])
const transport = new StdioClientTransport({
  command: process.execPath, args: [path.join(repo, 'mcp/agenthub/server.mjs')],
  env: { PATH: process.env.PATH, HOME: laptop, AGENTHUB_URL: BASE, AGENTHUB_TOKEN: token }
})
const client = new Client({ name: 'proof-453', version: '1.0.0' })
await client.connect(transport)
const call = async (name, args) => {
  const r = await client.callTool({ name, arguments: args })
  return { error: r.isError, json: JSON.parse(r.content[0].text) }
}
const tools = (await client.listTools()).tools.map(t => t.name)
check(tools.includes('session_take_local') && tools.includes('session_hand_back'), 'stdio server lists session_take_local and session_hand_back')

let r = await call('session_take_local', { sessionId: id, directory: project })
check(r.json.confirmationRequired === true && (await phase(id)) === 'Running',
  'unconfirmed take_local on a live session changes nothing and lists the steps', r.json.steps?.[0])

r = await call('session_take_local', { sessionId: id, directory: project, confirm: true })
const resumeId = r.json.resumeId
check(!r.error && r.json.paused === true && (await phase(id)) === 'Paused', 'confirmed take_local paused the session', `resumeId ${resumeId}`)
const written = r.json.files ?? []
check(written.length === 1 && written[0] === path.join(laptop, '.claude/projects/-' + project.slice(1).replace(/[^A-Za-z0-9]/g, '-'), `${resumeId}.jsonl`),
  'the conversation was written under the laptop\'s ~/.claude/projects for the local directory', written[0])
check(r.json.resumeCommand === `cd '${project}' && claude --resume ${resumeId}`, 'answer carries the resume command', r.json.resumeCommand)
const localFile = fs.readFileSync(written[0], 'utf8')
check(!localFile.includes('"cwd":"/workspace"') && localFile.includes(`"cwd":"${project}"`), 'every cwd in the local transcript is the local directory')
check((fs.statSync(written[0]).mode & 0o777) === 0o600, 'the local transcript is owner-only (0600)')

let before = requests()
const answer = localClaude(laptop, project, 'What was the code word?', resumeId)
await sleep(500)
const laptopRequest = lastRequest()
check(requests() === before + 1 && laptopRequest.includes('PINEAPPLE-7') && laptopRequest.includes('ANSWER-1'),
  'REAL `claude --resume` on the laptop sent the pod\'s earlier turn (code word + the pod\'s answer) to the model', answer)

// The hub refuses a hand-back next to a live pod: resume, try, pause again.
await api('POST', `/api/sessions/${id}/resume`)
await podTurn('Say ok.')
await waitForTurns(before + 2, 'the resumed pod')
r = await call('session_hand_back', { sessionId: id, directory: project })
check(r.error && r.json.error === 'session_live', 'hand_back while the session runs in the hub is refused', r.json.error)
r = await call('session_take_local', { sessionId: id, directory: project, confirm: true })
check(r.error && r.json.error === 'local_transcript_differs', 'taking it local again does not overwrite the local turns', r.json.error)

// That detour put a pod turn into the hub copy; start the local round again from the hub's state.
await call('session_take_local', { sessionId: id, directory: project, confirm: true, overwrite: true })
before = requests()
localClaude(laptop, project, 'Local turn: the deploy uses port 8443.', resumeId)
await podTurn('What did I say about the deploy port?')
r = await call('session_hand_back', { sessionId: id, directory: project })
check(!r.error && r.json.resumed === true, 'hand_back uploaded the local conversation and resumed the session', JSON.stringify(r.json).slice(0, 160))
await waitForTurns(before + 2, 'the pod resumed after the hand-back')
const podRequest = lastRequest()
check(podRequest.includes('8443') && podRequest.includes('PINEAPPLE-7'),
  'REAL `claude --resume` in the resumed pod sent both the hub turns and the laptop turn to the model')
const stored = transcriptIn(Buffer.from(await (await fetch(`${BASE}/api/remote/sessions/${id}/state`, { headers: { Authorization: `Bearer ${token}` } })).arrayBuffer()), '-workspace')
// Only the cwd fields decide where a CLI files and finds the conversation. Claude Code's own
// "Primary working directory: … (was …)" notes stay: they are how the model learns it moved.
const cwds = new Set(stored.split('\n').flatMap(line => { try { const o = JSON.parse(line); return 'cwd' in o ? [o.cwd] : [] } catch { return [] } }))
check(stored.includes('8443') && cwds.size === 1 && cwds.has('/workspace'),
  'the stored transcript is filed under the pod\'s folder again and every cwd is /workspace', [...cwds].join(', '))
check(stored.includes('(was /workspace)'), 'the CLI recorded the move itself, so the model was told about the other directory')
await client.close()

// ---------------------------------------------------------------- B. link path, the command as is
const laptopB = RIG + '/laptop-b', projectB = laptopB + '/proj'
fs.rmSync(laptopB, { recursive: true, force: true })
fs.mkdirSync(laptopB, { recursive: true })
claudeHome(laptopB, [projectB])
// The command ends in an interactive `claude --resume <id>`; a shim earlier on PATH turns that into
// one -p turn of the real CLI so the unmodified command can run here.
const shim = RIG + '/shim'
fs.mkdirSync(shim, { recursive: true })
fs.writeFileSync(shim + '/claude', `#!/bin/sh\nexec ${execSync('command -v claude', { encoding: 'utf8' }).trim()} -p "What was the code word, and the port?" "$@"\n`, { mode: 0o755 })
const shell = command => execFileSync('bash', ['-c', command], { env: { ...claudeEnv(laptopB), PATH: `${shim}:${process.env.PATH}` }, encoding: 'utf8', timeout: 180000 })

r = await api('POST', `/api/sessions/${id}/transfer/take-local`, { directory: projectB, confirm: true })
check(r.status === 200 && r.json.link?.url === `${INGRESS}/api/session-transfer` && r.json.link.secret?.startsWith('oat_')
  && r.json.link.command.includes(`-H 'X-AgentHub-Transfer: ${r.json.link.secret}'`),
  'web app take-local paused and returned a single-use link whose secret travels in a header, not the URL', r.json?.link?.url)
const takeSecret = r.json.link.secret
console.log('       command: ' + r.json.link.command.replace(/oat_\w+/g, 'oat_…'))
before = requests()
const out = shell(r.json.link.command)
const bRequest = lastRequest()
check(requests() === before + 1 && bRequest.includes('PINEAPPLE-7') && bRequest.includes('8443'),
  'the pasted command downloaded, unpacked and resumed: the real CLI sent the whole conversation', out.trim())
let again = 0
try { shell(`curl -fsS -o /dev/null -w '%{http_code}' -H 'X-AgentHub-Transfer: ${takeSecret}' ${r.json.link.url}`) } catch (e) { again = e.stdout?.toString() || e.status }
check(String(again).includes('404'), 'the link does not work a second time', String(again))

const back = await api('POST', `/api/sessions/${id}/transfer/hand-back-link`, { directory: projectB, resume: true })
console.log('       command: ' + back.json.link.command.replace(/oat_\w+/g, 'oat_…'))
await podTurn('Summarise what happened on the second laptop.')
before = requests()
const uploaded = JSON.parse(shell(back.json.link.command))
check(uploaded.resumed === true, 'the pasted hand-back command uploaded and resumed the session', JSON.stringify(uploaded))
await waitForTurns(before + 1, 'the pod resumed after the link hand-back')
// The links went through the ingress stand-in; its access log has every request line. Neither secret
// may be in it, nor in the backend's own log.
const accessLog = fs.readFileSync(RIG + '/access.log', 'utf8')
const linkLines = accessLog.split('\n').filter(line => line.includes('/api/session-transfer'))
const secrets = [takeSecret, back.json.link.secret]
check(linkLines.length >= 3 && !secrets.some(secret => accessLog.includes(secret)),
  'the ingress access log shows the link requests but neither secret', linkLines.slice(0, 3).map(l => l.split(' ').slice(1).join(' ')).join(' | '))
check(!secrets.some(secret => fs.readFileSync(RIG + '/backend.log', 'utf8').includes(secret)), 'the backend log holds neither secret')
check(lastRequest().includes('What was the code word, and the port?'), 'the resumed pod\'s real CLI sent the second laptop\'s turn too')

console.log(`\n${results.filter(Boolean).length}/${results.length} checks passed`)
process.exit(results.every(Boolean) ? 0 : 1)
