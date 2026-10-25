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

const SOURCES = { 'claude-otel': 'Claude Code', codex: 'Codex', opencode: 'OpenCode', openclaw: 'OpenClaw' }

/** The CLI a row's numbers come from ("Claude Code", "Codex", …), or '' when unknown. */
export function usageSource(row) {
  return SOURCES[row?.source] || ''
}

/**
 * What the cost column shows for a usage row (docs/usage-tracking.md). Money appears only for
 * API-billed usage, and only as the figure the CLI itself reported; a subscription shows its
 * turns, never a dollar amount, because the CLIs' price-table figure is not what anyone pays.
 *   { kind: 'api'|'subscription'|'custom'|'unknown'|'none', pill, text, title, usd }
 * usd is the amount that counts as spend (0 for everything but API-billed usage).
 */
export function sessionBilling(row) {
  const by = usageSource(row) || 'the CLI'
  const turns = Number(row?.turns) || 0
  const turnText = turns ? `${turns} turn${turns === 1 ? '' : 's'}` : 'no money'
  switch (row?.billing ?? (row?.apiBilled ? 'api' : null)) {
    case 'api': {
      const usd = Number(row.apiCostUsd ?? row.costUsd) || 0
      if (row.costReported === false) {
        return { kind: 'api', pill: 'api', usd: 0, text: 'not reported',
          title: `Billed against an API key. ${by} reports no cost, only tokens.` }
      }
      return { kind: 'api', pill: 'api', usd, text: formatCost(usd),
        title: `Billed against an API key — cost as reported by ${by}` }
    }
    case 'subscription':
      return { kind: 'subscription', pill: 'sub', usd: 0, text: turnText,
        title: 'Covered by a subscription — usage counts against its limits, not money' }
    case 'custom':
      return { kind: 'custom', pill: 'self-hosted', usd: 0, text: turnText,
        title: 'A self-hosted model endpoint — no provider bill' }
    case 'unknown':
      return { kind: 'unknown', pill: 'auto', usd: 0, text: 'billing unknown',
        title: `Auto: ${by} chose between the login and the API key itself` +
          ((Number(row.costUsd) || 0) > 0 ? ` (it reported ${formatCost(row.costUsd)} from its own price table)` : '') }
    default:
      return { kind: 'none', pill: '', usd: 0, text: '—', title: '' }
  }
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
