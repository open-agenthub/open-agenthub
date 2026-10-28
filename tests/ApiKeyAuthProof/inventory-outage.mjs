// Pauses only the explicitly selected rootless proof API server; always resumes it in finally.
import fs from 'node:fs'
import http from 'node:http'
import assert from 'node:assert/strict'
import { randomBytes } from 'node:crypto'
import { once } from 'node:events'
import { startBackend, waitHealthy } from './backend.mjs'

const pid = Number(process.env.PROOF_APISERVER_PID)
assert.ok(Number.isInteger(pid) && pid > 1, 'PROOF_APISERVER_PID must select the proof API server')
assert.equal(fs.readFileSync(`/proc/${pid}/comm`, 'utf8').trim(), 'kube-apiserver')
const rig = process.env.RIG || process.env.HOME + '/rig'
const log = rig + '/inventory-outage.log'
const key = randomBytes(32).toString('hex')
const wrong = 'synthetic-inventory-outage-key'
const sleep = ms => new Promise(resolve => setTimeout(resolve, ms))
const child = startBackend(process.argv[2], 'none', log, {
  Auth__ApiKey: key, ForwardedHeaders__KnownProxies: '127.0.0.1', ForwardedHeaders__KnownNetworks: ''
})
function request(source, xff, presented = wrong) {
  return new Promise((resolve, reject) => {
    const req = http.get({ hostname: '127.0.0.1', port: 8080, localAddress: source,
      path: '/api/admin/access', headers: { Authorization: `Bearer ${presented}`, 'X-Forwarded-For': xff } }, res => {
      res.resume()
      res.on('end', () => resolve(res.statusCode))
    })
    req.on('error', reject)
  })
}
let paused = false
try {
  assert.ok(await waitHealthy(child), 'backend did not become healthy')
  await sleep(500)
  for (let i = 0; i < 11; i++)
    assert.equal(await request('127.0.0.1', `203.0.113.${i + 1}`), 401)
  process.kill(pid, 'SIGSTOP')
  paused = true
  await sleep(5500)
  for (let i = 0; i < 10; i++)
    assert.equal(await request('127.0.0.1', `198.51.100.${i + 1}`), 401)
  assert.equal(await request('127.0.0.1', '198.51.100.99'), 429, 'outage must not trust rotating XFF')
  assert.equal(await request('127.0.0.2', '198.51.100.99', key), 200, 'another socket client is unaffected')
  process.kill(pid, 'SIGCONT')
  paused = false
  let status
  for (let i = 0; i < 60; i++) {
    status = await request('127.0.0.1', '192.0.2.200', key)
    if (status === 200) break
    await sleep(100)
  }
  assert.equal(status, 200, 'proxy separation must recover')
  await sleep(250)
  const text = fs.readFileSync(log, 'utf8')
  assert.equal(text.split('Forwarded-header workload inventory unavailable;').length - 1, 1, 'one outage warning')
  assert.equal(text.split('Forwarded-header workload inventory recovered;').length - 1, 1, 'one recovery log')
  assert.ok(!text.includes(key) && !text.includes(wrong), 'no credential in logs')
  console.log('PASS real API-server outage: socket fallback, independent socket client, recovery, one warning/recovery, no key disclosure')
} finally {
  if (paused) process.kill(pid, 'SIGCONT')
  child.kill('SIGTERM')
  await once(child, 'exit')
}
