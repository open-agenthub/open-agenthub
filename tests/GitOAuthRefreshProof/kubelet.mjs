// Kubelet stand-in for #289 (the rig has no kubelet): for each new session pod it does what the
// kubelet and the entrypoint do for git — mounts the gitcreds-<id> secret's `credentials` key as
// ~/.git-credentials (mode 600, credential.helper store) — trusts the mock's CA for git, starts the
// real session agent (agent.cjs) on the pod's own loopback address, and reports the pod Running
// with that address. Run: node kubelet.mjs <rig> <repo> <admin-kubeconfig>
import { execFileSync, spawn } from 'node:child_process'
import fs from 'node:fs'
import path from 'node:path'

const [rig, repo, kc] = process.argv.slice(2)
const here = path.dirname(new URL(import.meta.url).pathname)
const k = (...a) => execFileSync(process.env.KUBECTL || 'kubectl', ['--kubeconfig', kc, '-n', 'agenthub-sessions', ...a], { encoding: 'utf8' })
const seen = new Map()
let next = 2
const log = m => fs.appendFileSync(path.join(rig, 'kubelet.log'), new Date().toISOString() + ' ' + m + '\n')
for (;;) {
  try {
    const pods = JSON.parse(k('get', 'pods', '-l', 'agenthub.dev/component=agent', '-o', 'json')).items
    for (const p of pods) {
      const uid = p.metadata.uid
      if (seen.has(uid) || p.metadata.deletionTimestamp) continue
      const id = p.metadata.labels['agenthub.dev/session']
      const ip = `127.0.0.${next++}`
      const home = path.join(rig, 'pods', id, 'home')
      fs.mkdirSync(home, { recursive: true })
      const git = (...a) => execFileSync('git', a, { env: { ...process.env, HOME: home } })
      git('config', '--global', 'http.sslCAInfo', path.join(rig, 'mock/certs/bundle.crt'))
      git('config', '--global', 'user.name', 'proof'); git('config', '--global', 'user.email', 'proof@example.com')
      let mounted = false
      try {
        const b64 = k('get', 'secret', `gitcreds-${id}`, '-o', 'jsonpath={.data.credentials}')
        if (b64) {
          fs.writeFileSync(path.join(home, '.git-credentials'), Buffer.from(b64, 'base64'), { mode: 0o600 })
          git('config', '--global', 'credential.helper', 'store')
          mounted = true
        }
      } catch { /* no gitcreds secret: the session has no git credentials */ }
      const env = Object.fromEntries((p.spec.containers.find(c => c.name === 'agent').env || []).filter(e => 'value' in e).map(e => [e.name, e.value]))
      const agent = spawn(process.execPath, [path.join(here, 'agent.cjs')], {
        // NODE_PATH: the image's node_modules, which the runtime's own files resolve against there.
        env: { ...env, PATH: process.env.PATH, HOME: home, BIND_HOST: ip, RUNTIME: path.join(repo, 'agent-runtime'), NODE_PATH: '/opt/session-agent/node_modules' },
        stdio: ['ignore', fs.openSync(path.join(rig, 'pods', id, 'agent.log'), 'a'), fs.openSync(path.join(rig, 'pods', id, 'agent.log'), 'a')]
      })
      seen.set(uid, agent)
      fs.writeFileSync(path.join(rig, 'pods', id, 'agent.pid'), String(agent.pid))
      fs.writeFileSync(path.join(rig, 'pods', id, 'ip'), ip)
      await new Promise(r => setTimeout(r, 1500))
      k('patch', 'pod', p.metadata.name, '--subresource=status', '--type=merge', '-p',
        JSON.stringify({ status: { phase: 'Running', podIP: ip, podIPs: [{ ip }] } }))
      log(`running ${p.metadata.name} on ${ip}, gitcreds mounted: ${mounted}`)
    }
  } catch (e) { log('error ' + String(e.message).split('\n')[0]) }
  await new Promise(r => setTimeout(r, 500))
}
