export function sharedTokenFromPath(pathname) {
  const match = String(pathname || '').match(/^\/shared\/([^/]+)\/?$/)
  return match ? decodeURIComponent(match[1]) : null
}

// Settings tabs by URL segment. The lists live here rather than in SettingsView so that the
// URL parser and the subnav agree on what a valid tab is — an unknown segment must fall back
// to the default instead of rendering an empty settings body.
export const SETTINGS_DEFAULT_TAB = 'credentials'
export const PERSONAL_SETTINGS_TABS = ['profile', 'account', 'credentials', 'mcp', 'skills', 'notifications', 'tokens', 'webhooks']
export const ADMIN_SETTINGS_TABS = ['users', 'org-mcp', 'limits', 'groups', 'billing', 'license']

export function isSettingsTab(tab) {
  return PERSONAL_SETTINGS_TABS.includes(tab) || ADMIN_SETTINGS_TABS.includes(tab)
}

export function isAdminSettingsTab(tab) {
  return ADMIN_SETTINGS_TABS.includes(tab)
}

export function settingsPath(tab) {
  return `/settings/${isSettingsTab(tab) ? tab : SETTINGS_DEFAULT_TAB}`
}

// Returns the tab a settings URL points at, or null when the path is not a settings page.
// `/account` stays an alias: the backend's git OAuth callback redirects there, and changing
// that redirect would break instances whose backend is upgraded separately from the frontend.
export function settingsTabFromPath(pathname) {
  const path = String(pathname || '')
  if (path === '/account' || path === '/account/') return 'account'
  const match = path.match(/^\/settings(?:\/([^/]*))?\/?$/)
  if (!match) return null
  return isSettingsTab(match[1]) ? match[1] : SETTINGS_DEFAULT_TAB
}
