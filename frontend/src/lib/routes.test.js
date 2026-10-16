import { describe, expect, it } from 'vitest'
import {
  ADMIN_SETTINGS_TABS, PERSONAL_SETTINGS_TABS, SETTINGS_DEFAULT_TAB,
  isAdminSettingsTab, isSettingsTab, settingsPath, settingsTabFromPath, sharedTokenFromPath
} from './routes.js'

describe('sharedTokenFromPath', () => {
  it('extracts and decodes a secret-link token', () => {
    expect(sharedTokenFromPath('/shared/token%2Fvalue')).toBe('token/value')
  })

  it('ignores normal application routes', () => {
    expect(sharedTokenFromPath('/s/session-1')).toBeNull()
  })
})

describe('settingsTabFromPath', () => {
  it('maps every known tab to its own sub-path', () => {
    for (const tab of [...PERSONAL_SETTINGS_TABS, ...ADMIN_SETTINGS_TABS]) {
      expect(settingsTabFromPath(`/settings/${tab}`)).toBe(tab)
      expect(settingsTabFromPath(`/settings/${tab}/`)).toBe(tab)
    }
  })

  it('opens the default tab for the bare settings path', () => {
    expect(settingsTabFromPath('/settings')).toBe(SETTINGS_DEFAULT_TAB)
    expect(settingsTabFromPath('/settings/')).toBe(SETTINGS_DEFAULT_TAB)
  })

  it('falls back to the default tab for an unknown segment', () => {
    expect(settingsTabFromPath('/settings/nope')).toBe(SETTINGS_DEFAULT_TAB)
  })

  it('keeps /account as an alias of the connected-accounts tab', () => {
    expect(settingsTabFromPath('/account')).toBe('account')
    expect(settingsTabFromPath('/account/')).toBe('account')
  })

  it('returns null for non-settings routes', () => {
    expect(settingsTabFromPath('/')).toBeNull()
    expect(settingsTabFromPath('/s/session-1')).toBeNull()
    expect(settingsTabFromPath('/settings/mcp/extra')).toBeNull()
    expect(settingsTabFromPath('/settingsx')).toBeNull()
    expect(settingsTabFromPath('/accounts')).toBeNull()
    expect(settingsTabFromPath(undefined)).toBeNull()
  })
})

describe('settingsPath', () => {
  it('builds the canonical path for a tab', () => {
    expect(settingsPath('mcp')).toBe('/settings/mcp')
    expect(settingsPath('org-mcp')).toBe('/settings/org-mcp')
  })

  it('never produces a path for an unknown tab', () => {
    expect(settingsPath('nope')).toBe(`/settings/${SETTINGS_DEFAULT_TAB}`)
    expect(settingsPath(undefined)).toBe(`/settings/${SETTINGS_DEFAULT_TAB}`)
  })
})

describe('tab classification', () => {
  it('tells admin tabs from personal ones', () => {
    expect(isAdminSettingsTab('users')).toBe(true)
    expect(isAdminSettingsTab('credentials')).toBe(false)
    expect(isSettingsTab('license')).toBe(true)
    expect(isSettingsTab('nope')).toBe(false)
  })
})
