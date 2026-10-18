// When a Workspace message was written (#147): compact in the conversation, the full date on hover.
// The time is the provider's own — the timestamp Claude Code and Codex put on each transcript
// line — so a turn that waited in the CLI's queue shows when the CLI took it, not when it was typed.

function valid(value) {
  if (!value) return null
  const date = value instanceof Date ? value : new Date(value)
  return Number.isNaN(date.getTime()) ? null : date
}

// Today: the time alone. This year: day and time. Older: with the year. A conversation that runs
// past midnight therefore says which day a turn belongs to without repeating today's date.
export function shortMessageTime(value, { now = new Date(), locale } = {}) {
  const date = valid(value)
  if (!date) return ''
  const time = { hour: '2-digit', minute: '2-digit' }
  if (date.toDateString() === now.toDateString()) return date.toLocaleTimeString(locale, time)
  const day = date.getFullYear() === now.getFullYear()
    ? { month: 'short', day: 'numeric' }
    : { year: 'numeric', month: 'short', day: 'numeric' }
  return date.toLocaleString(locale, { ...day, ...time })
}

export function fullMessageTime(value, { locale } = {}) {
  const date = valid(value)
  return date ? date.toLocaleString(locale, { dateStyle: 'full', timeStyle: 'medium' }) : ''
}

export function isoMessageTime(value) {
  return valid(value)?.toISOString() || ''
}
