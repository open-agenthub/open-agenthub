// Ingress stand-in: forwards 127.0.0.1:8081 to the backend on 8080 and writes an access log line per
// request (method, path and query), as ingress controllers and proxies do. FrontendOrigin points
// here, so the transfer links go through it and the proof can check no secret reaches the log.
// Run: node access-proxy.mjs <logfile>
import fs from 'node:fs'
import http from 'node:http'
const [logfile] = process.argv.slice(2)
http.createServer((req, res) => {
  fs.appendFileSync(logfile, `${new Date().toISOString()} "${req.method} ${req.url} HTTP/${req.httpVersion}"\n`)
  const upstream = http.request({ host: '127.0.0.1', port: 8080, method: req.method, path: req.url, headers: req.headers }, answer => {
    res.writeHead(answer.statusCode, answer.headers)
    answer.pipe(res)
  })
  upstream.on('error', () => { res.writeHead(502); res.end() })
  req.pipe(upstream)
}).listen(8081, '127.0.0.1', () => console.log('access proxy on 8081'))
