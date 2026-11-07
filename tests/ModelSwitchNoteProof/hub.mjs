// Stand-in hub for the #515 browser proof: one running Claude session whose conversation is the
// page `page/` printed from a real transcript. Everything else the web app asks for is empty.
// Listens on both loopback families: Vite's proxy resolves localhost to ::1 first.
//
// usage: node hub.mjs <page.json>
import fs from 'node:fs'
import http from 'node:http'

const conversation = fs.readFileSync(process.argv[2], 'utf8')
const session = { id: 's1', title: 'Model switch note', agent: 'Claude', phase: 'Running', status: 'Running', mode: 'Interactive',
  uiMode: 'terminal', accessRole: 'Owner', createdAt: new Date().toISOString(), repos: [] }
const handler = (req, res) => {
  const url = req.url.split('?')[0]
  const json = body => { res.writeHead(200, { 'content-type': 'application/json' }); res.end(typeof body === 'string' ? body : JSON.stringify(body)) }
  if (url === '/api/config') return json({ authority: '' })
  if (url === '/api/sessions') return json([session])
  if (url === '/api/sessions/s1') return json(session)
  if (url === '/api/sessions/s1/conversation') return json(conversation)
  if (url === '/api/session-organization/order') return json([])
  if (/\/(permissions|messages|files|accounts|credentials|projects|sessions|shared)$/.test(url) || url.endsWith('/allowed')) return json([])
  return json({})
}
for (const host of ['127.0.0.1', '::1']) http.createServer(handler).listen(8080, host)
console.log('hub on 8080')
