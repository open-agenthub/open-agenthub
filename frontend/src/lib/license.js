// Pure helpers for the admin license/seat display, kept out of the component so they
// can be unit-tested without mounting Vue.

/// The badge state for the current license: 'active' | 'invalid' | 'off'.
export function licenseBadge(lic) {
  if (!lic) return 'off'
  if (lic.valid) return 'active'
  return lic.present ? 'invalid' : 'off'
}

export function licenseBadgeLabel(lic) {
  return { active: 'active', invalid: 'invalid', off: 'not activated' }[licenseBadge(lic)]
}

/// A licensed instance is over-booked when it has a seat cap and more seats are in use.
export function seatOverbooked(seats) {
  return !!seats && seats.included > 0 && seats.used > seats.included
}

// The settings tab where an admin activates a license — the one target every LicenseGate
// links to. Lives next to the other license helpers so the gate and the tests agree on it.
export const LICENSE_SETTINGS_TAB = 'license'
export const LICENSE_SETTINGS_PATH = `/settings/${LICENSE_SETTINGS_TAB}`

// The api layer answers every enterprise endpoint's 402 with a thrown Error carrying `.status`
// (and `.code`, see api.js). Components ask this instead of comparing the status themselves, so
// a future change of the signal (a code instead of a status) touches one line, not six panes.
export function isLicenseError(err) {
  return !!err && (err.status === 402 || err.code === 'license_required')
}
