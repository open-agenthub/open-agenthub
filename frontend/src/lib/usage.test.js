import { describe, it, expect } from 'vitest'
import { formatBytes, formatCpuSeconds, formatTokens, formatTokensExact, formatCost, hasResources, sessionBilling, totalTokens, percent, usageSource } from './usage.js'

describe('formatTokens', () => {
  it('keeps small numbers exact', () => {
    expect(formatTokens(0)).toBe('0')
    expect(formatTokens(999)).toBe('999')
  })
  it('abbreviates thousands', () => {
    expect(formatTokens(1200)).toBe('1.2K')
    expect(formatTokens(2000)).toBe('2K')
  })
  it('abbreviates millions and billions', () => {
    expect(formatTokens(1_500_000)).toBe('1.5M')
    expect(formatTokens(2_000_000_000)).toBe('2B')
  })
  it('handles undefined/garbage as 0', () => {
    expect(formatTokens(undefined)).toBe('0')
    expect(formatTokens(null)).toBe('0')
  })
})

describe('formatTokensExact', () => {
  it('adds thousands separators', () => {
    expect(formatTokensExact(1234567)).toBe('1,234,567')
    expect(formatTokensExact(0)).toBe('0')
  })
})

describe('formatCost', () => {
  it('formats normal amounts with 2 decimals', () => {
    expect(formatCost(1.5)).toBe('$1.50')
    expect(formatCost(0)).toBe('$0.00')
  })
  it('keeps precision for tiny amounts', () => {
    expect(formatCost(0.0021)).toBe('$0.0021')
  })
})

describe('totalTokens', () => {
  it('sums the four buckets', () => {
    expect(totalTokens({ inputTokens: 10, outputTokens: 20, cacheReadTokens: 5, cacheCreationTokens: 1 })).toBe(36)
  })
  it('handles missing fields and null', () => {
    expect(totalTokens({ inputTokens: 10 })).toBe(10)
    expect(totalTokens(null)).toBe(0)
  })
})

describe('sessionBilling', () => {
  it('shows the CLI-reported cost for API-billed rows, and counts it as spend', () => {
    const b = sessionBilling({ billing: 'api', apiCostUsd: 1.25, costUsd: 1.25, costReported: true, source: 'claude-otel' })
    expect(b).toMatchObject({ kind: 'api', pill: 'api', usd: 1.25, text: '$1.25' })
    expect(b.title).toContain('reported by Claude Code')
  })
  it('never shows money for a subscription row, even when the CLI reported a cost', () => {
    // Claude Code reports its price-table cost for a subscription login as well.
    const b = sessionBilling({ billing: 'subscription', costUsd: 0.003255, estimatedCostUsd: 5, turns: 3 })
    expect(b).toMatchObject({ kind: 'subscription', pill: 'sub', usd: 0, text: '3 turns' })
    expect(b.text).not.toContain('$')
    expect(sessionBilling({ billing: 'subscription' }).text).toBe('no money')
  })
  it('says "not reported" for an API row whose CLI computes no cost (Codex)', () => {
    expect(sessionBilling({ billing: 'api', costUsd: 0, costReported: false, source: 'codex' }))
      .toMatchObject({ kind: 'api', usd: 0, text: 'not reported' })
  })
  it('keeps an Auto row whose billing the CLI chose out of the spend', () => {
    const b = sessionBilling({ billing: 'unknown', costUsd: 2 })
    expect(b).toMatchObject({ kind: 'unknown', usd: 0, text: 'billing unknown' })
    expect(b.title).toContain('$2.00')
  })
  it('shows a self-hosted endpoint without money', () => {
    expect(sessionBilling({ billing: 'custom', turns: 1 })).toMatchObject({ kind: 'custom', usd: 0, text: '1 turn' })
  })
  it('handles rows without token usage', () => {
    expect(sessionBilling(null)).toMatchObject({ kind: 'none', usd: 0 })
    expect(sessionBilling({ billing: null })).toMatchObject({ kind: 'none' })
  })
})

describe('usageSource', () => {
  it('names the CLI the numbers come from', () => {
    expect(usageSource({ source: 'codex' })).toBe('Codex')
    expect(usageSource({ source: 'claude-otel' })).toBe('Claude Code')
    expect(usageSource({})).toBe('')
  })
})

describe('formatBytes', () => {
  it('keeps small values in bytes', () => {
    expect(formatBytes(0)).toBe('0 B')
    expect(formatBytes(1023)).toBe('1023 B')
  })
  it('scales through the binary units', () => {
    expect(formatBytes(1536)).toBe('1.5 KB')
    expect(formatBytes(104857600)).toBe('100 MB')
    expect(formatBytes(3.5 * 1024 ** 3)).toBe('3.5 GB')
  })
  it('handles undefined/garbage as 0', () => {
    expect(formatBytes(undefined)).toBe('0 B')
    expect(formatBytes(null)).toBe('0 B')
  })
})

describe('formatCpuSeconds', () => {
  it('shows plain seconds under a minute', () => {
    expect(formatCpuSeconds(0)).toBe('0s')
    expect(formatCpuSeconds(42.4)).toBe('42s')
  })
  it('switches to minutes and hours', () => {
    expect(formatCpuSeconds(90)).toBe('1m 30s')
    expect(formatCpuSeconds(600)).toBe('10m')
    expect(formatCpuSeconds(3720)).toBe('1h 2m')
    expect(formatCpuSeconds(7200)).toBe('2h')
  })
  it('clamps negatives and garbage to 0', () => {
    expect(formatCpuSeconds(-5)).toBe('0s')
    expect(formatCpuSeconds(undefined)).toBe('0s')
  })
})

describe('hasResources', () => {
  it('is true when any resource counter is set', () => {
    expect(hasResources({ cpuSeconds: 1 })).toBe(true)
    expect(hasResources({ peakMemoryBytes: 5 })).toBe(true)
    expect(hasResources({ rxBytes: 1 })).toBe(true)
    expect(hasResources({ txBytes: 1 })).toBe(true)
  })
  it('is false for old rows and null', () => {
    expect(hasResources({ inputTokens: 10 })).toBe(false)
    expect(hasResources(null)).toBe(false)
  })
})

describe('percent', () => {
  it('computes rounded percentage', () => {
    expect(percent(25, 100)).toBe(25)
    expect(percent(1, 3)).toBe(33)
  })
  it('returns 0 when total is 0', () => {
    expect(percent(5, 0)).toBe(0)
  })
})
