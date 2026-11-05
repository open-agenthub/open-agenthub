// @vitest-environment happy-dom
import { afterEach, beforeEach, expect, it, vi } from 'vitest'
import { enableAutoUnmount, mount } from '@vue/test-utils'
import McpConnectionPane from './McpConnectionPane.vue'
import SettingsView from './SettingsView.vue'
const { config } = vi.hoisted(() => ({ config: { mcpEnabled: true, mcpUrl: 'https://hub.example.com/mcp', mcpAuthMode: 'oidc' } }))
vi.mock('../api.js', () => ({ config, api: {}, auth: { user: 'alice' } }))
enableAutoUnmount(afterEach)
beforeEach(() => { Object.assign(config, { mcpEnabled: true, mcpUrl: 'https://hub.example.com/mcp', mcpAuthMode: 'oidc' }) })
it('copies canonical HTTP configuration for three clients without a token', async () => {
  const writeText = vi.fn().mockResolvedValue()
  Object.defineProperty(navigator, 'clipboard', { configurable: true, value: { writeText } })
  const w = mount(McpConnectionPane)
  expect(w.text()).toContain('sign in with your instance account')
  expect(w.get('[data-mcp-command]').text()).toBe("claude mcp add --transport http agenthub 'https://hub.example.com/mcp'")
  expect(JSON.parse(w.get('[data-mcp-cursor]').text())).toEqual({ mcpServers: { agenthub: { url: config.mcpUrl } } })
  expect(w.text()).toContain('Settings → Connectors → Add custom connector')
  for (const button of w.findAll('button')) await button.trigger('click')
  expect(writeText.mock.calls.map(c => c[0])).toEqual([w.get('[data-mcp-command]').text(), config.mcpUrl, w.get('[data-mcp-cursor]').text()])
})
it('shows a disabled endpoint hint only to administrators', () => {
  config.mcpEnabled = false
  expect(mount(McpConnectionPane).find('[data-mcp-disabled]').exists()).toBe(false)
  expect(mount(McpConnectionPane, { props: { isAdmin: true } }).text()).toContain('mcp.enabled: true')
  const stubs = { CredentialsDialog: true }
  expect(mount(SettingsView, { global: { stubs } }).find('[data-settings-tab="mcp-connect"]').exists()).toBe(false)
  expect(mount(SettingsView, { props: { isAdmin: true }, global: { stubs } }).find('[data-settings-tab="mcp-connect"]').exists()).toBe(true)
})
it('distinguishes API-key and insecure development login', () => {
  config.mcpAuthMode = 'apiKey'
  expect(mount(McpConnectionPane).text()).toContain('Enter the instance API key')
  config.mcpAuthMode = 'insecureDev'
  expect(mount(McpConnectionPane).text()).toContain('local dev user')
})
