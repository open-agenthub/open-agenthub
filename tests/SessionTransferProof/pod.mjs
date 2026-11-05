// Kubelet + session pod stand-in (no kubelet, no container runtime here). For every new session pod
// it does what the Claude runtime does, with the real pieces where they matter:
//   - resume: curl the pod's AGENTHUB_STATE_GET_URL and unpack it with the entrypoint's exact tar line;
//   - a turn: the REAL pinned Claude Code (`claude -p`, --resume when state was restored, else
//     --session-id) in the pod's AGENTHUB_WORKDIR, against mock-model.mjs;
//   - persist: the runtime's own archiveScript (agent-runtime/common/state-archive.js) with the
//     Claude driver's state dir and excludes, uploaded with uploadArgs to AGENTHUB_STATE_PUT_URL.
// The prompt for a pod's turn comes from $RIG/next-prompt.txt, so the proof decides what each pod says.
// Run: node pod.mjs <repo>
import { execFileSync } from 'node:child_process'
import fs from 'node:fs'
import { createRequire } from 'node:module'
import path from 'node:path'
import { claudeEnv, claudeHome } from './claude-env.mjs'

const [repo] = process.argv.slice(2)
const require = createRequire(import.meta.url)
const { archiveScript, uploadArgs } = require(path.join(repo, 'agent-runtime/common/state-archive.js'))
const driver = require(path.join(repo, 'agent-runtime/claude/driver.js'))
const RIG = process.env.RIG || process.env.HOME + '/rig'
const kubectl = process.env.KUBECTL || 'kubectl', kc = RIG + '/k8s/kubeconfig'
const k = (...a) => execFileSync(kubectl, ['--kubeconfig', kc, '-n', 'agenthub-sessions', ...a], { encoding: 'utf8' })
const log = m => fs.appendFileSync(RIG + '/pod.log', new Date().toISOString() + ' ' + m + '\n')
const seen = new Set()

for (;;) {
  try {
    for (const p of JSON.parse(k('get', 'pods', '-l', 'agenthub.dev/component=agent', '-o', 'json')).items) {
      const uid = p.metadata.uid
      if (seen.has(uid) || p.metadata.deletionTimestamp) continue
      seen.add(uid)
      const env = Object.fromEntries((p.spec.containers.find(c => c.name === 'agent').env || []).map(e => [e.name, e.value]))
      const home = path.join(RIG, 'pods', uid)
      fs.mkdirSync(home, { recursive: true })
      let restored = false
      if (env.AGENTHUB_RESUME === '1' && env.AGENTHUB_STATE_GET_URL) {
        try {
          execFileSync('curl', ['-fsS', '-o', home + '.tgz', env.AGENTHUB_STATE_GET_URL])
          // entrypoint-common.sh, verbatim apart from the paths.
          execFileSync('tar', ['xzf', home + '.tgz', '--exclude=.claude/backups', '--exclude=./.claude/backups', '-C', home])
          restored = true
        } catch (e) { log(`restore failed ${e.message}`) }
      }
      k('patch', 'pod', p.metadata.name, '--subresource=status', '--type=merge', '-p',
        JSON.stringify({ status: { phase: 'Running', podIP: '127.0.0.1', podIPs: [{ ip: '127.0.0.1' }] } }))
      const workdir = env.AGENTHUB_WORKDIR
      let sessionId = env.AGENTHUB_CLAUDE_SESSION_ID
      try { sessionId = fs.readFileSync(path.join(home, '.claude', 'agenthub-session-id'), 'utf8').trim() || sessionId } catch { }
      claudeHome(home, [workdir])
      const prompt = fs.readFileSync(path.join(RIG, 'next-prompt.txt'), 'utf8').trim()
      const args = ['-p', prompt, ...(restored ? ['--resume', sessionId] : ['--session-id', sessionId])]
      const out = execFileSync('claude', args, { cwd: workdir, env: claudeEnv(home), encoding: 'utf8', timeout: 120000 })
      fs.writeFileSync(path.join(home, '.claude', 'agenthub-session-id'), sessionId + '\n')
      const file = home + '.state.tgz'
      execFileSync('/bin/sh', ['-c', archiveScript(driver), 'sh', file], { cwd: home })
      execFileSync('curl', uploadArgs(file, env.AGENTHUB_STATE_PUT_URL))
      fs.writeFileSync(path.join(RIG, `pod-${uid}.done`), JSON.stringify({ sessionId, restored, workdir, out: out.trim() }))
      log(`pod ${p.metadata.name} restored=${restored} session=${sessionId} answer=${out.trim()}`)
    }
  } catch (e) { log('error ' + String(e.message).split('\n')[0]) }
  await new Promise(r => setTimeout(r, 500))
}
