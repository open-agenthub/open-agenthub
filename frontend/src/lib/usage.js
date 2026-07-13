// Pure formatting/aggregation helpers for the Usage dashboard (unit-tested).

/** Compact token count: 1234 -> "1.2K", 1500000 -> "1.5M", <1000 -> exact. */
export function formatTokens(n) {
  const v = Number(n) || 0
  const abs = Math.abs(v)
  if (abs < 1000) return String(Math.round(v))
  if (abs < 1_000_000) return trim(v / 1000) + 'K'
  if (abs < 1_000_000_000) return trim(v / 1_000_000) + 'M'
  return trim(v / 1_000_000_000) + 'B'
}

/** Exact token count with thousands separators, e.g. 1234567 -> "1,234,567". */
export function formatTokensExact(n) {
  return (Number(n) || 0).toLocaleString('en-US', { maximumFractionDigits: 0 })
}

/** USD cost. Small amounts keep more precision so tiny sessions are not shown as "$0.00". */
export function formatCost(usd) {
  const v = Number(usd) || 0
  const digits = v !== 0 && Math.abs(v) < 0.01 ? 4 : 2
  return '$' + v.toLocaleString('en-US', { minimumFractionDigits: 2, maximumFractionDigits: digits })
}

/** Sum of the four token buckets on a usage row. */
export function totalTokens(row) {
  if (!row) return 0
  return (Number(row.inputTokens) || 0)
    + (Number(row.outputTokens) || 0)
    + (Number(row.cacheReadTokens) || 0)
    + (Number(row.cacheCreationTokens) || 0)
}

/** Percentage (0..100, rounded) of part relative to total; 0 when total is 0. */
export function percent(part, total) {
  const t = Number(total) || 0
  if (t <= 0) return 0
  return Math.round(((Number(part) || 0) / t) * 100)
}

function trim(x) {
  // One decimal, but drop a trailing ".0" (e.g. 2.0K -> 2K).
  return (Math.round(x * 10) / 10).toString()
}
