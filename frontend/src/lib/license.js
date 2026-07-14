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
