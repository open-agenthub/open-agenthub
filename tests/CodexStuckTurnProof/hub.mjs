// Stand-in hub for the #562 browser proof: the web app's /api calls answered for one running
// Codex session, and that session's terminal socket proxied to the rig's real session agent.
import http from 'node:http'
import { createRequire } from 'node:module'
const require = createRequire(process.env.RT + '/package.json')
const { WebSocketServer, WebSocket } = require('ws')
const AGENT = Number(process.env.AGENT_PORT)
const session = { id: 's1', title: 'Demo audio', agent: 'Codex', phase: 'Running', status: 'Running', mode: process.env.MODE || 'Interactive',
  uiMode: 'terminal', accessRole: 'Owner', createdAt: new Date().toISOString(), repos: [] }
const server = http.createServer((req, res) => {
  const url = req.url.split('?')[0]

  const json = v => { res.writeHead(200, { 'content-type': 'application/json' }); res.end(JSON.stringify(v)) }
  if (url === '/api/config') return json({ authority: '' })
  if (url === '/api/sessions') return json([session])
  if (url === '/api/sessions/s1') return json(session)
  if (url === '/api/sessions/s1/conversation') return json({ source: 'native', entries: [], text: '', offset: 0, nextOffset: 0, length: 0, running: true })
  if (['/api/sessions/shared', '/api/session-organization/order', '/api/usage/sessions'].includes(url)) return json([])
  if (/\/(permissions|messages|files|accounts|credentials|projects|sessions)$/.test(url) || url.endsWith('/allowed')) return json([])
  return json({})
})
const wss = new WebSocketServer({ noServer: true, handleProtocols: () => 'tty' })
server.on('upgrade', (req, socket, head) => {
  const url = req.url.split('?')[0]
  wss.handleUpgrade(req, socket, head, client => {
    if (url !== '/ws/sessions/s1/terminal') return client.close()
    const query = req.url.includes('?') ? req.url.slice(req.url.indexOf('?')) : ''
    const upstream = new WebSocket(`ws://127.0.0.1:${AGENT}/${query}`, 'tty')
    const pending = []
    upstream.on('error', () => client.close())
    upstream.on('open', () => { for (const m of pending.splice(0)) upstream.send(m) })
    upstream.on('message', (d, binary) => client.readyState === 1 && client.send(binary ? d : d.toString()))
    client.on('message', d => { const m = d.toString(); if (/"type":"(interrupt|input)"/.test(m) && !/\\u001b\[/.test(m)) console.log('ws from browser:', m.slice(0, 120)); upstream.readyState === 1 ? upstream.send(m) : pending.push(m) })
    client.on('close', () => upstream.close())
    upstream.on('close', () => client.close())
  })
})
server.listen(8080, '127.0.0.1', () => console.log('hub on 8080 -> agent ' + AGENT))
