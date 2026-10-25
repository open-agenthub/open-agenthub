// Real branch runtime + pinned CLI + branch Workspace in the session's Chromium.
// PROOF_CDP and PROOF_BROWSER_URL name the session browser and a URL reachable from its pod.
import fs from 'node:fs'
import path from 'node:path'
import http from 'node:http'
import crypto from 'node:crypto'
import assert from 'node:assert/strict'
import { spawn } from 'node:child_process'
import { createRequire } from 'node:module'
const require = createRequire(import.meta.url)
const { chromium } = require('/opt/session-agent/node_modules/playwright-core')
const { WebSocket, WebSocketServer } = require('/opt/session-agent/node_modules/ws')
const root = path.resolve(import.meta.dirname, '../..')
const out = fs.mkdtempSync('/tmp/proof257-browser-')
const wait = ms => new Promise(resolve => setTimeout(resolve, ms))
const children = [], inputs = [], requests = []
let browser, model, hub, wss
const start = (cmd, args, options) => {
  const child = spawn(cmd, args, { ...options, stdio: ['ignore', 'pipe', 'pipe'] })
  children.push(child)
  child.stdout.on('data', data => fs.appendFileSync(path.join(out, path.basename(args[0]) + '.log'), data))
  child.stderr.on('data', data => fs.appendFileSync(path.join(out, path.basename(args[0]) + '.log'), data))
  return child
}
try {
  const runtime = path.join(out, 'agent-runtime')
  fs.cpSync(path.join(root, 'agent-runtime'), runtime, { recursive: true, filter: source => !source.split(path.sep).includes('node_modules') })
  for (const dir of [runtime, path.join(runtime, 'session-agent')]) fs.symlinkSync('/opt/session-agent/node_modules', path.join(dir, 'node_modules'))
  const serverFile = path.join(runtime, 'common/server.js')
  fs.writeFileSync(serverFile, fs.readFileSync(serverFile, 'utf8').replaceAll('/tmp/state.tgz', path.join(out, 'state.tgz')).replaceAll('/tmp/scrollback.log', path.join(out, 'scrollback.log')).replaceAll('/tmp/agenthub-auth-watcher.pid', path.join(out, 'watcher.pid')))
  model = http.createServer((req, res) => {
    let body = ''
    req.on('data', data => { body += data })
    req.on('end', () => {
      let json = {}; try { json = JSON.parse(body) } catch {}
      const suggestion = body.includes('[SUGGESTION MODE:')
      requests.push({ method: req.method, url: req.url, suggestion, body })
      if (req.url.includes('count_tokens')) return res.end('{"input_tokens":10}')
      if (!json.stream) { res.setHeader('content-type', 'application/json'); return res.end(JSON.stringify({ id: 'mock', type: 'message', role: 'assistant', content: [{ type: 'text', text: 'ok' }], stop_reason: 'end_turn', usage: { input_tokens: 1, output_tokens: 1 } })) }
      res.writeHead(200, { 'content-type': 'text/event-stream' })
      const ev = (type, data) => res.write('event: ' + type + '\ndata: ' + JSON.stringify({ type, ...data }) + '\n\n')
      ev('message_start', { message: { id: 'msg_' + crypto.randomUUID(), type: 'message', role: 'assistant', model: 'mock', content: [], stop_reason: null, usage: { input_tokens: 1, output_tokens: 1 } } })
      ev('content_block_start', { index: 0, content_block: { type: 'text', text: '' } })
      ev('content_block_delta', { index: 0, delta: { type: 'text_delta', text: suggestion ? 'run the tests' : 'The greeting is ready for testing.' } })
      ev('content_block_stop', { index: 0 })
      ev('message_delta', { delta: { stop_reason: 'end_turn' }, usage: { output_tokens: 1 } })
      ev('message_stop', {}); res.end()
    })
  }).listen(17110, '127.0.0.1')
  const session = { id: 's1', title: 'Native suggestion proof', agent: 'Claude', phase: 'Running', status: 'Running', mode: 'Interactive', uiMode: 'terminal', accessRole: 'Owner', createdAt: new Date().toISOString(), repos: [] }
  hub = http.createServer((req, res) => {
    const url = req.url.split('?')[0]
    res.setHeader('content-type', 'application/json')
    if (url === '/api/config') return res.end('{"authority":""}')
    if (url === '/api/sessions') return res.end(JSON.stringify([session]))
    if (url === '/api/sessions/s1') return res.end(JSON.stringify(session))
    if (url === '/api/sessions/s1/conversation') return res.end(JSON.stringify({ source: 'native', entries: [], text: '', offset: 0, nextOffset: 0, length: 0, running: true }))
    if (/\/(permissions|messages|files|accounts|credentials|projects|sessions)$/.test(url) || url.endsWith('/allowed')) return res.end('[]')
    res.end('{}')
  }).listen(8080, '127.0.0.1')
  wss = new WebSocketServer({ noServer: true, handleProtocols: () => 'tty' })
  hub.on('upgrade', (req, socket, head) => {
    if (!req.url.startsWith('/ws/sessions/s1/terminal')) return socket.destroy()
    wss.handleUpgrade(req, socket, head, client => {
      const upstream = new WebSocket('ws://127.0.0.1:17695/', 'tty'), pending = []
      upstream.on('error', () => client.close()); client.on('error', () => upstream.close())
      upstream.on('open', () => { for (const item of pending.splice(0)) upstream.send(item) })
      upstream.on('message', (data, binary) => { if (client.readyState === 1) client.send(binary ? data : data.toString()) })
      client.on('message', data => {
        const text = data.toString()
        try { const message = JSON.parse(text); if (message.type === 'input') inputs.push(message.data) } catch {}
        upstream.readyState === 1 ? upstream.send(text) : pending.push(text)
      })
      client.on('close', () => upstream.close()); upstream.on('close', () => client.close())
    })
  })
  const home = path.join(out, 'home'), work = path.join(out, 'work'), key = 'sk-ant-api03-' + 'x'.repeat(80)
  fs.mkdirSync(home); fs.mkdirSync(work)
  fs.writeFileSync(path.join(home, '.claude.json'), JSON.stringify({ hasCompletedOnboarding: true, theme: 'dark', customApiKeyResponses: { approved: [key.slice(-20)], rejected: [] }, projects: { [work]: { hasTrustDialogAccepted: true, hasCompletedProjectOnboarding: true } } }))
  const clean = Object.fromEntries(Object.entries(process.env).filter(([key]) => !/^(CLAUDE|AGENTHUB|ANTHROPIC|CODEX|OPENAI|NO_COLOR)/.test(key)))
  start(process.execPath, [serverFile], { cwd: work, env: { ...clean, HOME: home, FORCE_COLOR: '3', TERM: 'xterm-256color', COLORTERM: 'truecolor', PATH: (process.env.PROOF_CLIS || '/tmp/proof257-clis/node_modules/.bin') + ':' + process.env.PATH,
    ANTHROPIC_API_KEY: key, ANTHROPIC_BASE_URL: 'http://127.0.0.1:17110', AGENTHUB_DRIVER: path.join(runtime, 'claude/driver.js'), AGENTHUB_PORT: '17695', AGENTHUB_MODE: 'interactive', AGENTHUB_UI_MODE: 'terminal', AGENTHUB_AUTO_APPROVE: 'true', AGENTHUB_WORKDIR: work, AGENTHUB_WORKSPACE_ROOT: work, AGENTHUB_STATE_RESTORED: '0', AGENTHUB_SKILLS_SYNC_SECONDS: '0', AGENTHUB_SESSION_ID: 's1', AGENTHUB_CALLBACK_TOKEN: 'proof-token', AGENTHUB_CALLBACK_URL: 'http://127.0.0.1:8080/internal/sessions/s1', AGENTHUB_CLAUDE_SESSION_ID: crypto.randomUUID(), AGENTHUB_RESUME: '0' } })
  start(process.execPath, [path.join(root, 'frontend/node_modules/vite/bin/vite.js'), '--host', '0.0.0.0', '--strictPort'], { cwd: path.join(root, 'frontend'), env: process.env })
  for (let i = 0; i < 60; i++) { try { if ((await fetch('http://127.0.0.1:5173')).ok) break } catch {} await wait(250) }
  browser = await chromium.connectOverCDP(process.env.PROOF_CDP)
  const context = await browser.newContext({ viewport: { width: 1280, height: 900 } })
  const page = await context.newPage()
  page.on('pageerror', error => console.log('PAGE ERROR:', error.message))
  await page.addInitScript(() => localStorage.setItem('agenthub.preferredUi', 'workspace'))
  await page.goto(process.env.PROOF_BROWSER_URL + '/s/s1')
  const input = page.locator('[data-workspace-composer] textarea'), ghost = page.locator('[data-composer-suggestion]')
  await input.waitFor()
  await page.waitForFunction(() => !document.querySelector('[data-workspace-composer] textarea')?.disabled)
  // The real TUI answers focus queries on the hidden xterm connection too.
  await wait(10000)
  for (const prompt of ['Implement a greeting for testing', 'Finish the greeting']) { await input.fill(prompt); await input.press('Enter'); await wait(5000) }
  await ghost.waitFor({ timeout: 20000 })
  assert.equal(await ghost.textContent(), 'run the tests')
  assert.equal(await input.inputValue(), '')
  assert(requests.some(request => request.suggestion))
  await page.screenshot({ path: path.join(out, 'ghost.png') })
  const before = inputs.length
  await input.press('Enter'); await wait(300)
  assert.equal(inputs.length, before, 'Enter must not send unaccepted ghost text')
  for (const key of ['ArrowRight', 'Tab']) {
    if (key === 'Tab') { await input.fill('Create a fresh prediction'); await input.press('Enter'); await ghost.waitFor({ timeout: 20000 }) }
    const count = inputs.length
    await input.press(key); await wait(300)
    assert.equal(await input.inputValue(), 'run the tests')
    assert.equal(await ghost.count(), 0)
    assert.equal(inputs.length, count, 'acceptance must not write to the PTY')
    console.log('PASS:', key, 'accepts into an editable draft without PTY input')
  }
  await input.fill('run the tests carefully'); await input.press('Enter')
  await ghost.waitFor({ timeout: 20000 })
  assert(inputs.some(value => value.includes('run the tests carefully')))
  assert(requests.some(request => !request.suggestion && request.body.includes('run the tests carefully')))
  await input.fill('my own draft')
  assert.equal(await ghost.count(), 0)
  await input.fill(''); assert.equal(await ghost.count(), 0)
  await page.screenshot({ path: path.join(out, 'dismissed.png') })
  console.log('PASS: explicit edited send reaches model; typing dismisses native prediction')
  fs.writeFileSync(path.join(out, 'evidence.json'), JSON.stringify({ inputs, requests: requests.map(({ body, ...request }) => request) }, null, 2))
  console.log('Evidence:', out)
  await context.close()
} finally {
  fs.writeFileSync(path.join(out, 'traffic.json'), JSON.stringify({ inputs, requests }, null, 2))
  console.log('Artifacts:', out)
  await browser?.close()
  for (const child of children) child.kill('SIGTERM')
  for (const client of wss?.clients || []) client.terminate()
  wss?.close(); hub?.closeAllConnections(); hub?.close(); model?.closeAllConnections(); model?.close()
}
