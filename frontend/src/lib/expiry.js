// Pure helpers for a session's self-deletion deadline (docs/session-expiry.md).
// The backend stores seconds and reports `expiresAt`; the dialogs speak in hours and days.

export const UNIT_SECONDS = { hours: 3600, days: 86400 }
export const MIN_SECONDS = 300
export const MAX_SECONDS = 365 * 86400
export const SOON_MS = 60 * 60 * 1000

/** Seconds for a dialog value; null when the amount is not a positive number. */
export function toSeconds(amount, unit) {
  const n = Number(amount)
  const per = UNIT_SECONDS[unit]
  if (!per || !Number.isFinite(n) || n <= 0) return null
  return Math.round(n * per)
}

/**
 * Dialog value for a stored number of seconds: whole days when it divides evenly, hours
 * otherwise (rounded to a tenth, so a setting made through the API as "90m" shows as 1.5 h
 * rather than being silently rounded to 2).
 */
export function fromSeconds(seconds) {
  const s = Number(seconds)
  if (!Number.isFinite(s) || s <= 0) return { amount: 24, unit: 'hours' }
  if (s % 86400 === 0) return { amount: s / 86400, unit: 'days' }
  return { amount: Math.round((s / 3600) * 10) / 10, unit: 'hours' }
}

/** Default state of the Auto-delete card, from a session's stored setting (or none). */
export function autoDeleteForm(session) {
  const seconds = session?.autoDeleteAfterSeconds
  const enabled = Number.isFinite(Number(seconds)) && Number(seconds) > 0
  return {
    enabled,
    ...fromSeconds(enabled ? seconds : 0),
    // A scheduled session is never attached to, so the backend only accepts the start basis
    // there; defaulting to it keeps the card from offering a choice the API would refuse.
    from: session?.autoDeleteFrom === 'start' || session?.mode === 'Scheduled' ? 'start' : 'lastActivity'
  }
}

/**
 * The two request fields for a card state. `off` is what "disabled" becomes: 0 on an edit
 * (the backend reads 0 as "switch it off"), null on a create.
 */
export function autoDeletePayload(form, { off = null } = {}) {
  if (!form?.enabled) return { autoDeleteAfterSeconds: off, autoDeleteFrom: null }
  return { autoDeleteAfterSeconds: toSeconds(form.amount, form.unit), autoDeleteFrom: form.from }
}

/** "2 d 3 h", "45 min", "< 1 min", or "expired". Empty for no deadline. */
export function formatRemaining(expiresAt, now = Date.now()) {
  if (!expiresAt) return ''
  const ms = new Date(expiresAt).getTime() - now
  if (!Number.isFinite(ms)) return ''
  if (ms <= 0) return 'expired'
  const minutes = Math.floor(ms / 60000)
  if (minutes < 1) return '< 1 min'
  if (minutes < 60) return `${minutes} min`
  const hours = Math.floor(minutes / 60)
  if (hours < 24) {
    const rest = minutes % 60
    return rest ? `${hours} h ${rest} min` : `${hours} h`
  }
  const days = Math.floor(hours / 24)
  const restHours = hours % 24
  return restHours ? `${days} d ${restHours} h` : `${days} d`
}

/** Under an hour left (or already past): the badge switches to the warning colour. */
export function isExpiringSoon(expiresAt, now = Date.now()) {
  if (!expiresAt) return false
  const ms = new Date(expiresAt).getTime() - now
  return Number.isFinite(ms) && ms < SOON_MS
}
