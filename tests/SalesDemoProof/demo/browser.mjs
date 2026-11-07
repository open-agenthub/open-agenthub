// Real Chromium (Playwright's build, run in the session pod) reaching the guest through an SSH
// SOCKS proxy, so https://hub.example.com and https://id.example.com resolve inside the guest as
// they would on the team's network. The stand-in CA is trusted for exactly the two leaf keys
// (SPKI pins in $HOME/rig/m2/pki/spki.txt); nothing else bypasses certificate checks.
import { chromium } from 'playwright-core'
import fs from 'node:fs'

const HOME = process.env.HOME
export const HUB = 'https://hub.example.com'
// Exact origin, not a prefix: "https://hub.example.com.evil.example" also starts with HUB.
export const onHub = url => { try { return new URL(String(url)).origin === HUB } catch { return false } }
export async function launch() {
  const spki = fs.readFileSync(`${HOME}/rig/m2/pki/spki.txt`, 'utf8').trim()
  return chromium.launch({
    executablePath: process.env.CHROME || `${HOME}/.cache/ms-playwright/chromium-1194/chrome-linux/chrome`,
    args: ['--no-sandbox', '--disable-dev-shm-usage', `--proxy-server=socks5://127.0.0.1:${process.env.SOCKS_PORT || 11080}`,
      `--ignore-certificate-errors-spki-list=${spki}`, '--lang=en-US', '--font-render-hinting=none']
  })
}
export async function signIn(context, user, password) {
  const page = await context.newPage()
  await page.goto(HUB, { waitUntil: 'domcontentloaded' })
  await page.getByRole('button', { name: 'Sign in' }).click({ timeout: 60000 })
  await page.waitForSelector('#username, input[name=username]', { timeout: 120000 })
  await page.fill('#username', user)
  await page.fill('#password', password)
  await Promise.all([page.waitForURL(onHub, { timeout: 120000 }), page.click('#kc-login')])
  return page
}
