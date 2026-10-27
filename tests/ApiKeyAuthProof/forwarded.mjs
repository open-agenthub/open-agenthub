// Run against the real backend with the README's Postgres, TLS and Kubernetes prerequisites.
// A second loopback source models a direct caller that is outside the configured proxy list.
import http from 'node:http'
import fs from 'node:fs'
import assert from 'node:assert/strict'
import { once } from 'node:events'
import { startBackend, waitHealthy, readKey } from './backend.mjs'

const [dll, baseline] = process.argv.slice(2)
const rig = process.env.RIG || process.env.HOME + '/rig'
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
  process.env.ForwardedHeaders__KnownProxies = networks ? '' : '127.0.0.1'
  process.env.ForwardedHeaders__KnownNetworks = networks
  const log = `${rig}/forwarded-${vulnerable ? 'baseline' : networks ? 'network' : 'fixed'}.log`
  const child = startBackend(target, 'apiKey', log)
  try {
    assert.ok(await waitHealthy(child), `backend did not start; see ${log}`)
    for (let i = 0; i < 10; i++)
      check((await request('127.0.0.2', `203.0.113.${i + 1}`)).status, 401, 'initial direct attempt')
    check((await request('127.0.0.2', '198.51.100.99')).status, vulnerable ? 401 : 429,
      vulnerable ? 'baseline spoofed XFF evades limit' : 'untrusted rotating XFF cannot get a new bucket')
    if (vulnerable) return
    check((await request('127.0.0.2', '198.51.100.98')).retry, '60', 'limited response has Retry-After')
    for (let i = 0; i < 10; i++)
      check((await request('127.0.0.1', `192.0.2.${i + 1}, 203.0.113.70`)).status, 401, 'trusted rightmost client attempt')
    check((await request('127.0.0.1', '192.0.2.99, 203.0.113.70')).status, 429, 'invented left entry cannot bypass trusted bucket')
    check((await request('127.0.0.1', '203.0.113.70', readKey())).status, 429, 'correct key in its own exhausted bucket stays limited')
    check((await request('127.0.0.1', '203.0.113.71', readKey())).status, 200, 'another proxy client with correct key is unaffected')
    check((await request('127.0.0.3', '203.0.113.70', readKey())).status, 200, 'another direct client with correct key is unaffected')
    check((await request('127.0.0.1', 'forged-not-an-ip')).status, 401, 'malformed forwarded value falls back safely')
    check((await request('127.0.0.1', '203.0.113.72', wrong, '/api/sessions/%0D%0Aforged-line')).status, 401, 'encoded log-forging path is challenged')
  } finally {
    child.kill('SIGTERM')
    await once(child, 'exit')
    const text = fs.readFileSync(log, 'utf8')
    assert.ok(!text.includes(readKey()) && !text.includes(wrong), 'credentials leaked to logs')
    assert.ok(!/^forged-line/m.test(text), 'forged log line')
    console.log('PASS no credential disclosure or forged log lines')
  }
}
if (baseline) await run(baseline, true)
await run(dll)
await run(dll, false, '127.0.0.1/32')
console.log(`${checks} assertions passed against the real backend`)
