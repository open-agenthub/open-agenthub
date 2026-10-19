// Kubelet stand-in for the rig (no kubelet, no CNI): reports each new session pod as Running with
// a pod IP, and uploads a state archive the way the runtime does (real tar, `curl -fsS -T` to the
// pod's own presigned AGENTHUB_STATE_PUT_URL), so the backend's pause/resume finds a real object.
import { execFileSync } from 'node:child_process'
import fs from 'node:fs'
// RIG is the scratch directory of README.md; KUBECTL the kubectl binary.
const RIG = process.env.RIG || process.env.HOME + '/rig'
const kubectl = process.env.KUBECTL || 'kubectl', kc = RIG + '/k8s/kubeconfig'
const k = (...a) => execFileSync(kubectl, ['--kubeconfig', kc, '-n', 'agenthub-sessions', ...a], { encoding: 'utf8' })
const seen = new Set()
const log = m => fs.appendFileSync(RIG + '/kubelet.log', new Date().toISOString() + ' ' + m + '\n')
for (;;) {
  try {
    const pods = JSON.parse(k('get', 'pods', '-l', 'agenthub.dev/component=agent', '-o', 'json')).items
    for (const p of pods) {
      const uid = p.metadata.uid
      if (seen.has(uid) || p.metadata.deletionTimestamp) continue
      seen.add(uid)
      const env = Object.fromEntries((p.spec.containers.find(c => c.name === 'agent').env || []).map(e => [e.name, e.value]))
      const dir = fs.mkdtempSync(RIG + '/state-')
      fs.writeFileSync(dir + '/conversation.txt', `pod ${p.metadata.name} uid ${uid}\n`)
      execFileSync('tar', ['-czf', dir + '.tgz', '-C', dir, '.'])
      if (env.AGENTHUB_STATE_PUT_URL) execFileSync('curl', ['-fsS', '--max-time', '30', '-T', dir + '.tgz', env.AGENTHUB_STATE_PUT_URL])
      k('patch', 'pod', p.metadata.name, '--subresource=status', '--type=merge', '-p',
        JSON.stringify({ status: { phase: 'Running', podIP: '127.0.0.1', podIPs: [{ ip: '127.0.0.1' }] } }))
      log(`running ${p.metadata.name} ${uid} archive=${!!env.AGENTHUB_STATE_PUT_URL} resume=${env.AGENTHUB_RESUME || ''}`)
    }
  } catch (e) { log('error ' + String(e.message).split('\n')[0]) }
  await new Promise(r => setTimeout(r, 500))
}
