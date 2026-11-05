// The real Claude Code CLI with a fresh home and the mock model: no login, no prompts.
import fs from 'node:fs'
import path from 'node:path'
export const KEY = 'sk-ant-api03-' + 'x'.repeat(80)
export function claudeHome(home, directories) {
  fs.mkdirSync(home, { recursive: true })
  const file = path.join(home, '.claude.json')
  const config = fs.existsSync(file) ? JSON.parse(fs.readFileSync(file, 'utf8')) : {}
  config.hasCompletedOnboarding = true
  config.customApiKeyResponses = { approved: [KEY.slice(-20)], rejected: [] }
  config.projects = config.projects || {}
  for (const dir of directories) config.projects[dir] = { hasTrustDialogAccepted: true, hasCompletedProjectOnboarding: true }
  fs.writeFileSync(file, JSON.stringify(config))
}
export function claudeEnv(home, port = 17690) {
  // An AgentHub pod's own CLAUDE*/ANTHROPIC*/AGENTHUB* variables would leak into the child.
  const base = Object.fromEntries(Object.entries(process.env).filter(([k]) => !/^(CLAUDE|AGENTHUB|ANTHROPIC)/.test(k)))
  return { ...base, HOME: home, ANTHROPIC_API_KEY: KEY, ANTHROPIC_BASE_URL: `http://127.0.0.1:${port}`, DISABLE_AUTOUPDATER: '1' }
}
