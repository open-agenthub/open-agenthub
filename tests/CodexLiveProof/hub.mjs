// #452 stand-in hub on :8080 for the web app: canned answers for what the proof does not touch,
// /api/sessions/s1/conversation forwarded to the proof host (the REAL backend conversation path,
// :8090), the terminal socket proxied to the rig's real session agent. Control routes:
//   POST /__drop     destroys every proxied terminal socket (a dropped websocket, no close frame)
//   POST /__stall?n=N  the next N conversation requests are accepted and never answered — what a
//                    request on a dead connection looks like to the browser
import http from 'node:http'
import { createRequire } from 'node:module'
const require = createRequire('/opt/session-agent/package.json')
const { WebSocketServer, WebSocket } = require('ws')
const AGENT = Number(process.env.AGENT_PORT)
const session = { id: 's1', title: 'Codex proof', agent: 'Codex', phase: 'Running', status: 'Running', mode: 'Interactive',
  uiMode: 'terminal', accessRole: 'Owner', canWrite: true, owner: 'dev', createdAt: new Date().toISOString(), repos: [], podIp: '127.0.0.1' }
const sockets = new Set()
let stall = 0
const stalled = []
const stats = { conversation: 0, stalled: 0 }
const server = http.createServer((req, res) => {
  const [url, query = ''] = req.url.split('?')
  const json = v => { res.writeHead(200, { 'content-type': 'application/json' }); res.end(JSON.stringify(v)) }
  if (!globalThis.seen) globalThis.seen = new Set(); if (!globalThis.seen.has(url)) { globalThis.seen.add(url); console.log('api', JSON.stringify(req.method), JSON.stringify(url)) }
  if (url === '/__drop') { const n = sockets.size; for (const s of sockets) s.destroy(); sockets.clear(); return json({ dropped: n }) }
  if (url === '/__stall') { stall = Number(new URLSearchParams(query).get('n') || 1); return json({ stall }) }
  if (url === '/__stats') return json(stats)
  if (url === '/api/config') return json({ authority: '' })
  if (url === '/api/sessions') return json([session])
  if (url === '/api/sessions/s1') return json(session)
  if (url === '/api/sessions/s1/conversation') {
    stats.conversation++
    if (stall > 0) { stall--; stats.stalled++; stalled.push(res); return }
    const up = http.request({ host: '127.0.0.1', port: 8090, path: req.url, method: 'GET' }, r => { res.writeHead(r.statusCode, r.headers); r.pipe(res) })
    up.on('error', () => { res.writeHead(502); res.end() })
    return up.end()
  }
  if (/\/(permissions|messages|files|accounts|credentials|projects|sessions|commands|shared|groups|order)$/.test(url) || url.endsWith('/allowed')) return json([])
  return json({})
})
const wss = new WebSocketServer({ noServer: true, handleProtocols: () => 'tty' })
server.on('upgrade', (req, socket, head) => {
  const url = req.url.split('?')[0]
  wss.handleUpgrade(req, socket, head, client => {
    if (url !== '/ws/sessions/s1/terminal') return client.close()
    sockets.add(socket)
    socket.on('close', () => sockets.delete(socket))
    const upstream = new WebSocket(`ws://127.0.0.1:${AGENT}/`)
    const pending = []
    upstream.on('open', () => { for (const m of pending.splice(0)) upstream.send(m) })
    upstream.on('message', (d, binary) => client.readyState === 1 && client.send(binary ? d : d.toString()))
    client.on('message', d => upstream.readyState === 1 ? upstream.send(d.toString()) : pending.push(d.toString()))
    client.on('close', () => upstream.close())
    upstream.on('close', () => client.close())
    upstream.on('error', () => client.close())
  })
})
server.listen(8080, '127.0.0.1', () => console.log('hub on 8080 -> agent ' + AGENT))
