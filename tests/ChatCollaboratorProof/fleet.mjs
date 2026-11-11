import assert from 'node:assert/strict'
import { readFileSync } from 'node:fs'
import { createRequire } from 'node:module'
import { setTimeout as delay } from 'node:timers/promises'

const require = createRequire('/opt/session-agent/package.json')
const { Client } = require('@modelcontextprotocol/sdk/client/index.js')
const { StdioClientTransport } = require('@modelcontextprotocol/sdk/client/stdio.js')
const WebSocket = require('ws')
const [expected, modelLog] = process.argv.slice(2)
assert.ok(['before', 'after'].includes(expected))
const run = Date.now()
const hub = process.env.PROOF_URL ?? 'http://127.0.0.1:17680'

async function share(path, body) {
  const response = await fetch(hub + '/api/ee/sessions/pairing/shares/' + path, {
    method: 'POST',
    headers: { Cookie: 'proof-user=alice', 'Content-Type': 'application/json' },
    body: JSON.stringify(body)
  })
  assert.ok(response.ok, await response.clone().text())
  return response.json()
}

async function tool(script, env, name, args) {
  const client = new Client({ name: 'shared-chat-proof', version: '1.0.0' })
  const transport = new StdioClientTransport({ command: process.execPath, args: [script], env })
  try {
    await client.connect(transport)
    const result = await client.callTool({ name, arguments: args })
    return { failed: result.isError === true, body: JSON.parse(result.content[0].text) }
  } finally {
    await client.close()
  }
}

function sendAgent(sender, message, options = {}) {
  return tool('agent-runtime/sessions/server.mjs', {
    AGENTHUB_CALLBACK_URL: hub + '/internal/sessions/' + sender,
    AGENTHUB_CALLBACK_TOKEN: 'callback-token-' + sender,
    AGENTHUB_SESSION_ID: sender
  }, 'agent_send', { to: 'pairing', message, ...options })
}

function sendLink(link, message) {
  return tool('mcp/agenthub/server.mjs', { AGENTHUB_URL: hub }, 'share_link_send', { link, message })
}

async function waitForModel(text) {
  for (let attempts = 0; attempts < 150; attempts++) {
    if (readFileSync(modelLog, 'utf8').includes(text)) return
    await delay(200)
  }
  assert.fail('Message did not reach the model: ' + text)
}

async function composer(socket, text) {
  socket.send(JSON.stringify({ type: 'chat', text, clientTurnId: text }))
  await waitForModel(text)
  await delay(1000)
}

async function messageRows() {
  const response = await fetch(hub + '/proof/messages')
  assert.ok(response.ok)
  return response.json()
}

const previous = await messageRows()
await share('users', { recipient: 'bob', role: 'Collaborator' })
await share('users', { recipient: 'carol', role: 'Viewer' })
const collaborator = await share('links', { role: 'Collaborator' })
const viewer = await share('links', { role: 'Viewer' })
const socket = new WebSocket(hub.replace(/^http/, 'ws') + '/ws/sessions/pairing/terminal', {
  headers: { Cookie: 'proof-user=bob' }
})
try {
  await new Promise((resolve, reject) => { socket.once('open', resolve); socket.once('error', reject) })
  await composer(socket, `composer-positive-control-first-${run}`)
  const owned = await sendAgent('owned', 'owned-fleet-marker-' + run)
  const named = await sendAgent('sender', 'named-fleet-marker-' + run)
  const link = await sendLink(collaborator.url, 'link-fleet-marker-' + run)
  for (const result of [owned, named, link]) {
    assert.equal(result.failed, expected === 'after', JSON.stringify(result))
    if (expected === 'before') assert.equal(result.body.deliveredVia, 'inbox')
    else {
      assert.equal(result.body.error, 'chat_inbox_unsupported')
      assert.match(result.body.message, /Type into its conversation as the owner or a Collaborator/)
    }
  }
  assert.equal(owned.body.reason, expected === 'before' ? 'not_priority' : undefined)
  assert.deepEqual(await sendAgent('viewer', 'viewer-named-marker'),
    { failed: true, body: { error: 'sessions_backend_http_404' } })
  assert.deepEqual(await sendLink(viewer.url, 'viewer-link-marker'),
    { failed: true, body: { error: 'agenthub_http_404' } })

  const priorityMarker = 'owned-priority-marker-' + run
  const priority = await sendAgent('owned', priorityMarker, { priority: true })
  assert.equal(priority.failed, false, JSON.stringify(priority))
  assert.equal(priority.body.deliveredVia, 'injected')
  await waitForModel(priorityMarker)
  await composer(socket, 'composer-positive-control-second-' + run)
  assert.doesNotMatch(readFileSync(modelLog, 'utf8'), /(?:fleet|viewer-(?:named|link))-marker/)

  const rows = await messageRows()
  for (const row of previous) assert.deepEqual(rows.find(current => current.id === row.id), row)
  const added = rows.filter(row => !previous.some(old => old.id === row.id))
  const pending = added.filter(row => row.deliveredAt == null)
  assert.deepEqual(pending.map(row => row.body).sort(), expected === 'before'
    ? ['link', 'named', 'owned'].map(kind => kind + '-fleet-marker-' + run) : [])
  assert.equal(added.length, pending.length + 1)
  assert.equal(added.find(row => row.body === priorityMarker)?.deliveredVia, 'injected')
  console.log(expected, { owned, named, link, priority: priority.body.deliveredVia,
    newPendingRows: pending.length, preservedRows: previous.length, viewerDenials: 2, composerTurns: 2 })
} finally {
  socket.close()
}
