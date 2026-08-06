import { describe, it, expect } from 'vitest'
import { formatApproxCost, formatBytes, formatCpuSeconds, formatTokens, formatTokensExact, formatCost, hasResources, sessionCost, totalTokens, percent } from './usage.js'

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

describe('sessionCost', () => {
  it('shows the real reported cost for API-billed rows', () => {
    expect(sessionCost({ apiBilled: true, costUsd: 1.25, estimatedCostUsd: 2 }))
      .toEqual({ usd: 1.25, approx: false })
  })
  it('shows the estimate, marked approximate, for subscription rows', () => {
    expect(sessionCost({ apiBilled: false, costUsd: 0, estimatedCostUsd: 3.5 }))
      .toEqual({ usd: 3.5, approx: true })
  })
  it('falls back to costUsd>0 when the apiBilled flag is missing (old rows)', () => {
    expect(sessionCost({ costUsd: 0.4 })).toEqual({ usd: 0.4, approx: false })
    expect(sessionCost({ costUsd: 0, estimatedCostUsd: 1 })).toEqual({ usd: 1, approx: true })
  })
  it('handles null rows', () => {
    expect(sessionCost(null)).toEqual({ usd: 0, approx: false })
  })
})

describe('formatApproxCost', () => {
  it('prefixes the cost with a tilde', () => {
    expect(formatApproxCost(1.5)).toBe('~$1.50')
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
