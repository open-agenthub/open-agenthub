// The PowerShell variants of the link commands, run unmodified under real pwsh. On Linux: curl.exe
// is a shim for curl, so this checks the command's syntax and flow under real pwsh, not
// Windows itself. Needs a session left paused by proof.mjs: node powershell.mjs <pwsh> <sessionId>
import { execFileSync, execSync } from 'node:child_process'
import fs from 'node:fs'
import os from 'node:os'
import { claudeEnv, claudeHome } from './claude-env.mjs'

const [pwsh, id] = process.argv.slice(2)
const RIG = process.env.RIG || process.env.HOME + '/rig'
const BASE = 'http://127.0.0.1:8080'
const home = RIG + '/laptop-ps', project = home + "/it's here"
fs.rmSync(home, { recursive: true, force: true })
fs.mkdirSync(home + '/tmp', { recursive: true })
claudeHome(home, [project])
const shim = RIG + '/shim-ps'
fs.mkdirSync(shim, { recursive: true })
fs.writeFileSync(shim + '/curl.exe', '#!/bin/sh\nexec curl "$@"\n', { mode: 0o755 })
fs.writeFileSync(shim + '/claude', `#!/bin/sh\nexec ${execSync('command -v claude', { encoding: 'utf8' }).trim()} -p "Which port?" "$@"\n`, { mode: 0o755 })
const env = { ...claudeEnv(home), PATH: `${shim}:${process.env.PATH}`, DOTNET_SYSTEM_GLOBALIZATION_INVARIANT: '1' }
const ps = command => execFileSync(pwsh, ['-NoProfile', '-NonInteractive', '-Command', command], { env, encoding: 'utf8', timeout: 180000 })
const post = async (p, body) => (await fetch(BASE + p, { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify(body) })).json()

const take = await post(`/api/sessions/${id}/transfer/take-local`, { directory: project, confirm: true })
console.log('take:   ' + take.link.powerShellCommand.replace(/oat_\w+/, 'oat_…'))
const answer = ps(take.link.powerShellCommand).trim()
const file = `${home}/.claude/projects/${project.replace(/[^A-Za-z0-9]/g, '-')}/${take.resumeId}.jsonl`
console.log(`${fs.existsSync(file) && /ANSWER-\d+/.test(answer) ? 'PASS' : 'FAIL'} PowerShell take-local wrote the transcript and the real CLI resumed it — ${answer}`)
const back = await post(`/api/sessions/${id}/transfer/hand-back-link`, { directory: project, resume: false })
console.log('back:   ' + back.link.powerShellCommand.replace(/oat_\w+/, 'oat_…'))
const result = JSON.parse(ps(back.link.powerShellCommand))
console.log(`${result.sessionId === id && result.resumed === false ? 'PASS' : 'FAIL'} PowerShell hand-back uploaded — ${JSON.stringify(result)}`)
console.log(`${fs.readdirSync(os.tmpdir()).some(f => f.startsWith('agenthub-' + id)) ? 'FAIL' : 'PASS'} the temporary archive was removed`)
