// The session pod for the proof: the branch's real common/server.js with the real Claude driver
// and the pinned Claude Code, started with AGENTHUB_UI_MODE as the pod spec sets it (chat or
// terminal), against mock-model.mjs. Paths an image would provide move into a scratch directory.
// Run: node pod.mjs <scratch dir> <chat|terminal> <agent port>
//   env: REPO (worktree), CLAUDE_BIN_DIR (directory with the pinned `claude`), MODEL_PORT
import fs from 'node:fs'
import path from 'node:path'
import { spawn } from 'node:child_process'

const [R, uiMode, agentPort] = process.argv.slice(2)
const REPO = process.env.REPO
const MODEL_PORT = process.env.MODEL_PORT || '17695'
fs.rmSync(R, { recursive: true, force: true })
const home = path.join(R, 'home'), work = path.join(R, 'work'), SA = path.join(R, 'sa/agent-runtime')
for (const d of [home, work, path.join(home, '.claude'), path.join(R, 'tmp')]) fs.mkdirSync(d, { recursive: true })
fs.cpSync(path.join(REPO, 'agent-runtime'), SA, { recursive: true, filter: src => path.basename(src) !== 'node_modules' })
fs.symlinkSync('/opt/session-agent/node_modules', path.join(SA, 'node_modules'))

const key = 'sk-ant-api03-' + 'x'.repeat(80)
fs.writeFileSync(path.join(home, '.claude.json'), JSON.stringify({
  hasCompletedOnboarding: true, theme: 'dark', customApiKeyResponses: { approved: [key.slice(-20)], rejected: [] },
  projects: { [work]: { hasTrustDialogAccepted: true } }
}))
const base = Object.fromEntries(Object.entries(process.env).filter(([n]) => !/^(CLAUDE|AGENTHUB|ANTHROPIC|OTEL)/.test(n)))
const env = {
  ...base, HOME: home, TERM: 'xterm-256color', TMPDIR: path.join(R, 'tmp'),
  PATH: `${process.env.CLAUDE_BIN_DIR}:${process.env.PATH}`, RUNTIME: SA,
  ANTHROPIC_API_KEY: key, ANTHROPIC_BASE_URL: `http://127.0.0.1:${MODEL_PORT}`,
  AGENTHUB_DRIVER: path.join(SA, 'claude/driver.js'), AGENTHUB_UI_MODE: uiMode, AGENTHUB_MODE: 'interactive',
  AGENTHUB_PORT: agentPort, AGENTHUB_SESSION_ID: process.env.SESSION_ID || 'pairing',
  AGENTHUB_CALLBACK_URL: process.env.CALLBACK_URL || 'http://127.0.0.1:9/internal/sessions/pairing',
  AGENTHUB_CALLBACK_TOKEN: process.env.CALLBACK_TOKEN || 'callback-token-pairing',
  AGENTHUB_AUTH_WATCHER_PIDFILE: path.join(R, 'tmp/watcher.pid'),
  AGENTHUB_WORKDIR: work, CLAUDE_CODE_ENABLE_TELEMETRY: '0'
}
const child = spawn(process.execPath, [path.join(SA, 'common/server.js')], { cwd: work, env, stdio: ['ignore', 'pipe', 'pipe'] })
child.stdout.on('data', d => fs.appendFileSync(path.join(R, 'agent.log'), d))
child.stderr.on('data', d => fs.appendFileSync(path.join(R, 'agent.log'), d))
child.on('exit', code => { fs.appendFileSync(path.join(R, 'agent.log'), `server.js exited ${code}\n`); process.exit(0) })
for (const s of ['SIGTERM', 'SIGINT']) process.on(s, () => { child.kill('SIGTERM'); setTimeout(() => process.exit(0), 3000) })
console.log(`pod (${uiMode}) on ${agentPort} from ${R}`)
