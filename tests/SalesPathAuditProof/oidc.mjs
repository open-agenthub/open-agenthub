// Signs a Keycloak user into the hub in its own browser context and prints what they see.
//   node oidc.mjs <user> <password> <shot-prefix>
import { chromium } from 'playwright-core'
const [user, pw, shot] = process.argv.slice(2)
const b = await chromium.connectOverCDP('http://127.0.0.1:19222')
const ctx = await b.newContext({ viewport: { width: 1440, height: 900 } })
const p = await ctx.newPage()
p.on('console', m => { if (m.type() !== 'debug') console.log('console', m.type(), m.text().slice(0, 300)) })
p.on('framenavigated', f => { if (f === p.mainFrame()) console.log('nav', f.url().slice(0, 160)) })
p.on('response', r => { if (r.status() >= 400) console.log('http', r.status(), r.url().slice(0, 140)) })
const t0 = Date.now(); const log = (...a) => console.log(((Date.now() - t0) / 1000).toFixed(1) + 's', ...a)
const txt = async () => (await p.locator('body').innerText()).replace(/\s+/g, ' ').slice(0, 900)
await p.goto('http://localhost:8080/', { waitUntil: 'networkidle' })
log('landing', p.url().slice(0, 80), await txt())
if (!p.url().includes('kc.example.test')) { const btn = p.getByRole('button', { name: /sign in/i }).first(); if (await btn.count()) await btn.click() }
await p.waitForURL(/kc\.example\.test/, { timeout: 30000 })
await p.fill('#username', user); await p.fill('#password', pw); await p.click('#kc-login')
await p.waitForURL(/localhost:8080/, { timeout: 60000 }); await p.waitForTimeout(6000)
await p.screenshot({ path: `${process.env.SHOTS}/${shot}-home.png` })
log('home', await txt())
await p.locator('text=⚙').first().click(); await p.waitForTimeout(3000)
const st = await txt(); log('settings shows ADMIN section:', /ADMIN/.test(st)); await p.screenshot({ path: `${process.env.SHOTS}/${shot}-settings.png` })
const us = p.getByText('Users & seats').first(); if (await us.count()) { await us.click(); await p.waitForTimeout(3000); log('users&seats', (await txt()).split('Users & seats').pop().slice(0, 700)); await p.screenshot({ path: `${process.env.SHOTS}/${shot}-seats.png`, fullPage: true }) }
await p.goto('http://localhost:8080/', { waitUntil: 'networkidle' })
const ov = await p.evaluate(async () => (await fetch('/api/admin/overview', { headers: { } })).status)
log('admin overview status (no bearer, cookie-less)', ov)
await b.close().catch(() => {})
