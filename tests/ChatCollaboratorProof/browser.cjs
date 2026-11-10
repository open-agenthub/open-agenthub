// #495 in Chromium: alice owns a running chat-mode (uiMode "chat") session and has shared it with
// bob as Collaborator and with carol as Viewer, through the real sharing API. Each user opens the session in the web app the way they would — alice
// from her list, bob and carol from "Shared with me" at /s/<id> — and types into the composer.
// What reached the agent is read from the mock model's log, not from the page.
// Env: UI (default http://localhost:5173), PW_MODULE (playwright-core), CHROME, OUT (screenshots),
//      MODEL_LOG (mock-model.mjs's model.log), EXPECT (before|after),
//      UI_MODE (chat, default; terminal for the comparison run with a terminal-backed pod and host).
const fs = require('node:fs')
const { chromium } = require(process.env.PW_MODULE || 'playwright-core')
const UI = process.env.UI || 'http://localhost:5173'
const OUT = process.env.OUT || '.'
const EXPECT = process.env.EXPECT || 'after'
const UI_MODE = process.env.UI_MODE || 'chat'
// The frame the composer must send for this transport: a chat turn, or keystrokes for the PTY.
const FRAME = UI_MODE === 'chat' ? '"type":"chat"' : '"type":"input"'
const stamp = Date.now().toString(36).toUpperCase()
const results = []
const check = (name, ok, detail = '') => { results.push({ name, ok: !!ok, detail }); console.log(`${ok ? 'PASS' : 'FAIL'} ${name}${detail ? ' — ' + detail : ''}`) }
const modelSaw = text => fs.existsSync(process.env.MODEL_LOG) && fs.readFileSync(process.env.MODEL_LOG, 'utf8').includes(JSON.stringify(text))
const until = async (fn, ms = 20000) => { const end = Date.now() + ms; while (Date.now() < end) { if (await fn()) return true; await new Promise(r => setTimeout(r, 250)) } return false }

async function open(browser, user) {
  const ctx = await browser.newContext({ viewport: { width: 1280, height: 900 } })
  await ctx.addCookies([{ name: 'proof-user', value: user, url: UI }])
  const page = await ctx.newPage()
  const sent = []
  page.on('websocket', ws => { if (ws.url().includes('/terminal')) ws.on('framesent', f => sent.push(String(f.payload))) })
  await page.goto(`${UI}/s/pairing`)
  await page.getByText('Pairing').first().waitFor({ timeout: 20000 })
  return { page, sent }
}

async function type(page, text) {
  const box = page.locator('textarea:visible').last()
  await box.waitFor({ timeout: 15000 })
  await page.waitForTimeout(1500)
  await box.fill(text)
  await box.press('Enter')
}

;(async () => {
  const browser = await chromium.launch({ executablePath: process.env.CHROME, args: ['--no-sandbox', '--disable-dev-shm-usage'] })

  const alice = await open(browser, 'alice')
  for (const [recipient, role] of [['bob', 'Collaborator'], ['carol', 'Viewer']]) {
    const share = await alice.page.request.post(`${UI}/api/ee/sessions/pairing/shares/users`, { data: { recipient, role } })
    check(`alice shares the session with ${recipient} as ${role}`, share.ok(), `HTTP ${share.status()}`)
  }
  const listed = await (await (await open(browser, 'bob')).page.request.get(`${UI}/api/sessions/shared`)).json()
  const seen = listed.find(s => s.id === 'pairing')
  console.log(`  bob's /api/sessions/shared: accessRole=${seen?.accessRole} uiMode=${seen?.uiMode}`)
  if (EXPECT === 'before') check('before: the shared listing reports uiMode "terminal" for a chat session (the cause)', seen?.uiMode === 'terminal')
  else check(`the shared listing reports the session's uiMode "${UI_MODE}"`, seen?.uiMode === UI_MODE)
  const owner = `OWNER-${stamp}`
  await type(alice.page, owner)
  check('owner: the message reaches the agent', await until(() => modelSaw(owner)))
  check(`owner: the page sent ${FRAME}`, alice.sent.some(f => f.includes(FRAME) && f.includes(owner)))
  await alice.page.screenshot({ path: `${OUT}/495-${UI_MODE}-${EXPECT}-1-owner.png` })

  const bob = await open(browser, 'bob')
  const collab = `COLLAB-${stamp}`
  await type(bob.page, collab)
  const reached = await until(() => modelSaw(collab))
  const chatFrame = bob.sent.some(f => f.includes('"type":"chat"') && f.includes(collab))
  const inputFrame = bob.sent.some(f => f.includes('"type":"input"') && f.includes(collab))
  console.log(`  bob's frames: chat=${chatFrame} input=${inputFrame}`)
  await bob.page.screenshot({ path: `${OUT}/495-${UI_MODE}-${EXPECT}-2-collaborator.png` })
  if (EXPECT === 'before') {
    check('before: the Collaborator message does NOT reach the agent (the bug)', !reached)
    check('before: the Collaborator page sent terminal input, not a chat frame', inputFrame && !chatFrame)
  } else {
    check('collaborator: the message reaches the agent', reached)
    check(`collaborator: the page sent ${FRAME} only`, UI_MODE === 'chat' ? chatFrame && !inputFrame : inputFrame && !chatFrame)
    check('collaborator: the reply is shown on the collaborator page',
      await until(async () => (await bob.page.locator('body').innerText()).includes(`RECEIVED: ${collab}`)))
  }

  const carol = await open(browser, 'carol')
  await carol.page.waitForTimeout(4000)
  const boxes = await carol.page.locator('textarea:visible').count()
  check('viewer: no composer to type into', boxes === 0, `${boxes} visible textarea(s)`)
  const viewerText = `VIEWER-${stamp}`
  // A hand-made frame on the viewer's own socket: the proxy must not forward it.
  await carol.page.evaluate(text => new Promise(resolve => {
    const ws = new WebSocket(`${location.protocol === 'https:' ? 'wss' : 'ws'}://${location.host}/ws/sessions/pairing/terminal`)
    ws.onopen = () => { ws.send(JSON.stringify({ type: 'chat', text, clientTurnId: 'v1' })); ws.send(JSON.stringify({ type: 'input', data: text + '\r' })); setTimeout(resolve, 500) }
    ws.onerror = () => resolve()
  }), viewerText)
  check('viewer: a hand-made chat/input frame does not reach the agent', !(await until(() => modelSaw(viewerText), 8000)))
  await carol.page.screenshot({ path: `${OUT}/495-${UI_MODE}-${EXPECT}-3-viewer.png` })

  await browser.close()
  const failed = results.filter(r => !r.ok)
  console.log(`${results.length - failed.length}/${results.length} passed (${UI_MODE}, ${EXPECT})`)
  process.exit(failed.length ? 1 : 0)
})().catch(e => { console.error(e); process.exit(2) })
