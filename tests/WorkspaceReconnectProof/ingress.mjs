// Stand-in for the ingress in front of two backend replicas (#520). The demo rig where the
// Workspace stuck at "disconnected" ran two replicas behind the k3s ingress at a load of 30 on
// 4 vCPUs. A replica starved that hard takes a request and does not answer it, and the ingress
// has no response-header timeout for a WebSocket upgrade, so the browser's handshake just waits.
//
// Here WebSocket upgrades are balanced round-robin over the real backend and a "starved replica"
// that holds the connection and never answers; plain HTTP goes to the real backend so the rest of
// the app keeps working. A fresh connection (a reload, or a retry) has an even chance to reach the
// healthy replica; one that landed on the starved one waits until the browser gives up on it.
//
// Run: node ingress.mjs <listen port> <backend port>   (e.g. 8080 8081; Vite proxies to :8080)
// STARVED=0 sends every upgrade to the backend (one healthy replica).
import http from 'node:http'
import net from 'node:net'

const [listen = '8080', backend = '8081'] = process.argv.slice(2)
const held = []
const starved = process.env.STARVED !== '0'
let upgrades = 0

const server = http.createServer((req, res) => {
  const upstream = http.request({ host: '127.0.0.1', port: Number(backend), method: req.method, path: req.url, headers: req.headers }, answer => {
    res.writeHead(answer.statusCode, answer.headers)
    answer.pipe(res)
  })
  upstream.on('error', () => { if (!res.headersSent) res.writeHead(502); res.end() })
  req.pipe(upstream)
})

server.on('upgrade', (req, socket, head) => {
  const n = ++upgrades
  if (starved && n % 2 === 0) {
    console.log(`[ingress] upgrade #${n} ${req.url.split('?')[0]} -> starved replica (held)`)
    held.push(socket)
    socket.on('error', () => {})
    socket.on('close', () => console.log(`[ingress] upgrade #${n} closed by the browser`))
    return
  }
  console.log(`[ingress] upgrade #${n} ${req.url.split('?')[0]} -> backend`)
  const upstream = net.connect(Number(backend), '127.0.0.1', () => {
    const lines = [`${req.method} ${req.url} HTTP/1.1`]
    for (let i = 0; i < req.rawHeaders.length; i += 2) lines.push(`${req.rawHeaders[i]}: ${req.rawHeaders[i + 1]}`)
    upstream.write(lines.join('\r\n') + '\r\n\r\n')
    if (head?.length) upstream.write(head)
    upstream.pipe(socket)
    socket.pipe(upstream)
  })
  upstream.on('error', () => socket.destroy())
  socket.on('error', () => upstream.destroy())
})

server.listen(Number(listen), '127.0.0.1', () => console.log(`[ingress] :${listen} -> backend :${backend}${starved ? ' + a starved replica' : ''}`))
