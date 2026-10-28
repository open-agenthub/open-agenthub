// Run against the real backend with the README's Postgres, TLS and Kubernetes prerequisites.
// A second loopback source models a direct caller that is outside the configured proxy list.
import http from 'node:http'
import fs from 'node:fs'
import assert from 'node:assert/strict'
import { randomBytes } from 'node:crypto'
import { once } from 'node:events'
import { execFileSync } from 'node:child_process'
import { startBackend, waitHealthy } from './backend.mjs'

const [dll, baseline] = process.argv.slice(2)
const rig = process.env.RIG || process.env.HOME + '/rig'
const key = randomBytes(32).toString('hex')
const wrong = 'synthetic-wrong-key-proof327'
let checks = 0
function check(actual, expected, label) {
  assert.equal(actual, expected, label)
  checks++
  console.log(`PASS ${label}`)
}
function request(source, xff, key = wrong, path = '/api/admin/access') {
  return new Promise((resolve, reject) => {
    const req = http.get({ hostname: '127.0.0.1', port: 8080, localAddress: source, path,
      headers: { Authorization: `Bearer ${key}`, 'X-Forwarded-For': xff } }, res => {
      res.resume()
      res.on('end', () => resolve({ status: res.statusCode, retry: res.headers['retry-after'] }))
    })
    req.on('error', reject)
  })
}
async function run(target, vulnerable = false, networks = '') {
  process.env.ForwardedHeaders__KnownProxies = networks ? '' : '127.0.0.1,127.0.0.4,127.0.0.5,127.0.0.6'
  process.env.ForwardedHeaders__KnownNetworks = networks
  const log = `${rig}/forwarded-${vulnerable ? 'baseline' : networks ? 'network' : 'fixed'}.log`
  const child = startBackend(target, 'none', log, { Auth__ApiKey: key })
  try {
    assert.ok(await waitHealthy(child), `backend did not start; see ${log}`)
    for (let i = 0; i < 10; i++)
      check((await request('127.0.0.2', `203.0.113.${i + 1}`)).status, 401, 'initial direct attempt')
    check((await request('127.0.0.2', '198.51.100.99')).status, vulnerable ? 401 : 429,
      vulnerable ? 'baseline spoofed XFF evades limit' : 'untrusted rotating XFF cannot get a new bucket')
    if (vulnerable) return
    const lateName = 'forwarded-proof327-late'
    addPod(lateName, 'agent', '127.0.0.6')
    let status
    for (let i = 0; i < 40; i++) {
      status = (await request('127.0.0.6', `192.0.2.${i + 1}`)).status
      if (status === 429) break
      await new Promise(resolve => setTimeout(resolve, 100))
    }
    check(status, 429, 'watch discovers newly assigned workload IP')
    kubectl(['delete', 'pod', lateName, '--grace-period=0', '--force', '--wait=false'])
    for (let i = 0; i < 40; i++) {
      status = (await request('127.0.0.6', '198.51.100.220', key)).status
      if (status === 200) break
      await new Promise(resolve => setTimeout(resolve, 100))
    }
    check(status, 200, 'watch removes deleted workload IP')
    check((await request('127.0.0.2', '198.51.100.98')).retry, '60', 'limited response has Retry-After')
    for (let i = 0; i < 10; i++)
      check((await request('127.0.0.1', `192.0.2.${i + 1}, 203.0.113.70`)).status, 401, 'trusted rightmost client attempt')
    check((await request('127.0.0.1', '192.0.2.99, 203.0.113.70')).status, 429, 'invented left entry cannot bypass trusted bucket')
    check((await request('127.0.0.1', '203.0.113.70', key)).status, 429, 'correct key in its own exhausted bucket stays limited')
    check((await request('127.0.0.1', '203.0.113.71', key)).status, 200, 'another proxy client with correct key is unaffected')
    check((await request('127.0.0.3', '203.0.113.70', key)).status, 200, 'another direct client with correct key is unaffected')
    for (const source of ['127.0.0.4', '127.0.0.5']) {
      for (let i = 0; i < 10; i++)
        check((await request(source, `198.51.100.${i + 1}`)).status, 401, 'workload initial attempt')
      check((await request(source, '198.51.100.99')).status, 429, 'workload inside trusted range cannot rotate XFF')
    }
    check((await request('127.0.0.1', 'forged-not-an-ip')).status, 401, 'malformed forwarded value falls back safely')
    check((await request('127.0.0.1', '203.0.113.72', wrong, '/api/sessions/%0D%0Aforged-line')).status, 401, 'encoded log-forging path is challenged')
  } finally {
    child.kill('SIGTERM')
    await once(child, 'exit')
    const text = fs.readFileSync(log, 'utf8')
    assert.ok(!text.includes(key) && !text.includes(wrong), 'credentials leaked to logs')
    assert.ok(!/^forged-line/m.test(text), 'forged log line')
    console.log('PASS no credential disclosure or forged log lines')
  }
}
function kubectl(args, input) {
  return execFileSync(process.env.KUBECTL || 'kubectl', ['-n', 'agenthub-sessions', ...args],
    { input, stdio: ['pipe', 'pipe', 'pipe'] })
}
const names = []
function addPod(name, component, source) {
  kubectl(['apply', '-f', '-'], JSON.stringify({ apiVersion: 'v1', kind: 'Pod',
    metadata: { name, labels: { 'agenthub.dev/component': component } },
    spec: { containers: [{ name: 'stand-in', image: 'registry.example.com/proof:unused' }] } }))
  names.push(name)
  kubectl(['patch', 'pod', name, '--subresource=status', '--type=merge', '-p',
    JSON.stringify({ status: { phase: 'Running', podIP: source, podIPs: [{ ip: source }] } })])
}
try {
  if (baseline) await run(baseline, true)
  for (const [component, source] of [['agent', '127.0.0.4'], ['browser', '127.0.0.5']])
    addPod(`forwarded-proof327-${component}`, component, source)
  await run(dll)
  await run(dll, false, '127.0.0.1/32,127.0.0.4/30')
} finally {
  for (const name of names) kubectl(['delete', 'pod', name, '--ignore-not-found', '--grace-period=0', '--force', '--wait=false'])
}
console.log(`${checks} assertions passed against the real backend`)
