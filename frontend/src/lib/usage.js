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

/**
 * The cost to show for a usage row. API-billed sessions show the real reported cost;
 * subscription sessions show the estimated API-equivalent, marked approximate.
 * Rows from before the split (no estimate) fall back to the reported cost.
 */
export function sessionCost(row) {
  if (!row) return { usd: 0, approx: false }
  const apiBilled = row.apiBilled ?? ((Number(row.costUsd) || 0) > 0)
  if (apiBilled) return { usd: Number(row.costUsd) || 0, approx: false }
  return { usd: Number(row.estimatedCostUsd) || 0, approx: true }
}

/** "~$1.23" — an estimated (subscription-covered) amount. */
export function formatApproxCost(usd) {
  return '~' + formatCost(usd)
}

/** Byte count for memory/network: 1536 -> "1.5 KB", 0 -> "0 B". */
export function formatBytes(n) {
  const v = Number(n) || 0
  if (v < 1024) return Math.round(v) + ' B'
  const units = ['KB', 'MB', 'GB', 'TB']
  let value = v
  let unit = 'B'
  for (const next of units) {
    if (value < 1024) break
    value /= 1024
    unit = next
  }
  return trim(value) + ' ' + unit
}

/** CPU time in seconds: 42 -> "42s", 90 -> "1m 30s", 3700 -> "1h 2m". */
export function formatCpuSeconds(seconds) {
  const v = Math.max(0, Number(seconds) || 0)
  if (v < 60) return Math.round(v) + 's'
  const minutes = Math.floor(v / 60)
  if (minutes < 60) {
    const rest = Math.round(v - minutes * 60)
    return rest > 0 ? `${minutes}m ${rest}s` : `${minutes}m`
  }
  const hours = Math.floor(minutes / 60)
  const restMinutes = minutes - hours * 60
  return restMinutes > 0 ? `${hours}h ${restMinutes}m` : `${hours}h`
}

/** True when a usage row carries any pod resource data (older rows have none). */
export function hasResources(row) {
  if (!row) return false
  return (Number(row.cpuSeconds) || 0) > 0
    || (Number(row.peakMemoryBytes) || 0) > 0
    || (Number(row.rxBytes) || 0) > 0
    || (Number(row.txBytes) || 0) > 0
}

function trim(x) {
  // One decimal, but drop a trailing ".0" (e.g. 2.0K -> 2K).
  return (Math.round(x * 10) / 10).toString()
}
