// Serves the hand-over card alone from the branch's Vite toolchain, for a screenshot of its text in
// a real browser (#574). The full proof.mjs needs a backend, OIDC, a kube-apiserver and object
// storage; for a change to the card's own wording the backend's answer is stubbed in the page.
// The stub replaces fetch in the page rather than intercepting in a driver, so any browser works —
// including a session browser in another pod, which cannot be scripted with route interception.
//
//   node intro-card.mjs <repo> [port]      then open http://<this-host>:<port>/
import { mkdtempSync, writeFileSync } from 'node:fs'
import { tmpdir } from 'node:os'
import { join } from 'node:path'
import { pathToFileURL } from 'node:url'

const [repo, port = '5173'] = process.argv.slice(2)
const frontend = join(repo, 'frontend')
const nm = (p) => pathToFileURL(join(frontend, 'node_modules', p)).href
const { createServer } = await import(nm('vite/dist/node/index.js'))
const vue = (await import(nm('@vitejs/plugin-vue/dist/index.mjs'))).default

// The entry lives outside the tree, so nothing the proof generates lands in the repository.
const root = mkdtempSync(join(tmpdir(), 'handover-card-'))
writeFileSync(join(root, 'index.html'), `<!doctype html><meta charset="utf-8"><title>Hand-over card</title>
<div id="app" style="max-width:760px;margin:24px"></div><script type="module" src="/main.js"></script>`)
writeFileSync(join(root, 'main.js'), `import { createApp } from 'vue'
import '/@fs${frontend}/src/style.css'
import HandoverCard from '/@fs${frontend}/src/components/HandoverCard.vue'
const overview = { available: true, history: [],
  sources: [{ owner: 'admin', sessions: 3, projects: 1 }],
  targets: [{ owner: 'alice', displayName: 'Alice', email: 'alice@example.com' }] }
const real = window.fetch
window.fetch = (url, init) => String(url).endsWith('/api/admin/handover')
  ? Promise.resolve(new Response(JSON.stringify(overview), { headers: { 'content-type': 'application/json' } }))
  : real(url, init)
createApp(HandoverCard).mount('#app')`)

const server = await createServer({
  root, configFile: false, logLevel: 'error', plugins: [vue()],
  // The entry's own imports resolve from its temp directory, which has no node_modules.
  resolve: { alias: { vue: join(frontend, 'node_modules/vue') } },
  server: { host: '0.0.0.0', port: Number(port), strictPort: true, allowedHosts: true, fs: { allow: [root, frontend] } }
})
await server.listen()
console.log(`serving the hand-over card on port ${port}`)
