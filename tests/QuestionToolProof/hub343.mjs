// #343: questionPending follows /notify (not for an idle notification) and /turn-started exactly
// as InternalController does.
// Stand-in hub on :8080: session metadata for the SPA, the terminal WebSocket relayed to the
// session agent, and the internal callbacks the hooks and the session agent make.
// usage: node hub.mjs <AgentName>
import http from 'node:http'
import fs from 'node:fs'
import path from 'node:path'
import { createRequire } from 'node:module'
const require = createRequire(import.meta.url)
const { WebSocketServer, WebSocket } = require('/opt/session-agent/node_modules/ws')
const R = path.dirname(new URL(import.meta.url).pathname)
const agentName = process.argv[2] || 'Claude'
const session = { id: 's1', title: 'Activity proof', agent: agentName, phase: 'Running', status: 'Running', mode: 'Interactive',
  uiMode: 'terminal', accessRole: 'Owner', createdAt: new Date().toISOString(), repos: [] }
const trace = line => { fs.appendFileSync(path.join(process.env.RIG_HOME || R, 'hub.log'), new Date().toISOString() + ' ' + line + '\n') }
const body = req => new Promise(resolve => { const c = []; req.on('data', d => c.push(d)); req.on('end', () => resolve(Buffer.concat(c))) })

const server = http.createServer(async (req, res) => {
  const url = req.url.split('?')[0]
  const json = (v, status = 200) => { res.writeHead(status, { 'content-type': 'application/json' }); res.end(JSON.stringify(v)) }
  if (url === '/api/config') return json({ authority: '' })
  if (url === '/rig/session') return json(session)
  if (url === '/api/sessions') return json([session])
  if (url === '/api/sessions/shared') return json([])
  if (url === '/api/session-organization/order') return json([])
  if (url === '/api/sessions/s1') return json(session)
  if (url === '/api/sessions/s1/conversation') return json({ source: 'native', entries: [], text: '', offset: 0, nextOffset: 0, length: 0, running: true })
  // The approval endpoint the PreToolUse hook asks. Unavailable here, so the hook falls back to
  // the CLI's own dialog ("ask"), and every request it would have filed is logged.
  if (url === '/internal/sessions/s1/permission') { trace('permission request ' + (await body(req)).toString()); res.writeHead(503); return res.end() }
  if (url === '/internal/sessions/s1/notify') { const b = (await body(req)).toString(); trace('notify ' + b); if (JSON.parse(b).idle !== true) session.questionPending = true; return json({}) }
  if (url === '/internal/sessions/s1/turn-started') { trace('turn-started'); session.questionPending = false; await body(req); return json({}) }
  if (url.startsWith('/internal/')) { trace('internal ' + req.method + ' ' + url); await body(req); return json({}) }
  if (/\/(permissions|messages|files|accounts|credentials|projects|sessions)$/.test(url) || url.endsWith('/allowed')) return json([])
  return json({})
})
const wss = new WebSocketServer({ noServer: true, handleProtocols: () => 'tty' })
server.on('upgrade', (req, socket, head) => {
  const url = req.url.split('?')[0]
  wss.handleUpgrade(req, socket, head, client => {
    if (url !== '/ws/sessions/s1/terminal') return
    const upstream = new WebSocket('ws://127.0.0.1:17694/', 'tty')
    const pending = []
    upstream.on('open', () => { for (const msg of pending.splice(0)) upstream.send(msg) })
    upstream.on('message', (d, binary) => client.readyState === 1 && client.send(binary ? d : d.toString()))
    client.on('message', d => {
      const text = d.toString()
      try { const msg = JSON.parse(text); if (msg.type === 'input') trace('ws input ' + JSON.stringify(msg.data)) } catch {}
      upstream.readyState === 1 ? upstream.send(text) : pending.push(text)
    })
    client.on('close', () => upstream.close())
    upstream.on('close', () => client.close())
  })
})
server.listen(8080, '127.0.0.1', () => console.log('hub on 8080 for', agentName))
