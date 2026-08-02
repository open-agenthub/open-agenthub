import { describe, expect, it } from 'vitest'
import { toTranscriptBlocks } from './transcript.js'

describe('toTranscriptBlocks', () => {
  it('turns large whitespace runs into ordered blocks and removes single blank layout lines', () => {
    const text = 'first line\n \n\t\nsecond line\n\nthird line'

    expect(toTranscriptBlocks(text)).toEqual([
      'first line',
      'second line\nthird line'
    ])
  })

  it('normalizes carriage returns to line feeds', () => {
    expect(toTranscriptBlocks('alpha\r\nbeta\rgamma')).toEqual([
      'alpha\nbeta\ngamma'
    ])
  })

  it('preserves whitespace on non-empty terminal lines', () => {
    expect(toTranscriptBlocks('  prompt  \n\n output \t')).toEqual([
      '  prompt  \n output \t'
    ])
  })

  it('returns no blocks for missing or whitespace-only text', () => {
    expect(toTranscriptBlocks(null)).toEqual([])
    expect(toTranscriptBlocks(' \n\t\n')).toEqual([])
  })
})
