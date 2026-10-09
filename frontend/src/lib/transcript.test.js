import { describe, expect, it } from 'vitest'
import { entryLabel, toTranscriptBlocks, toTranscriptItems } from './transcript.js'

const separated = (...blocks) => blocks.join('\n\n\n')

describe('toTranscriptItems', () => {
  it('renders native entries one to one, labelled by role, with the tool name', () => {
    const page = {
      source: 'native',
      entries: [
        { role: 'user', text: 'go' },
        { role: 'assistant', text: 'ok' },
        { role: 'tool', text: '{"command":"ls"}', tool: 'Bash' },
        { role: 'result', text: '·' },
        { role: 'something_new', text: 'x' }
      ]
    }

    expect(toTranscriptItems(page).map(item => [item.role, item.label, item.text])).toEqual([
      ['user', 'User', 'go'],
      ['assistant', 'Agent', 'ok'],
      ['tool', 'Tool · Bash', '{"command":"ls"}'],
      ['result', 'Result', '·'],
      ['something_new', 'something_new', 'x']
    ])
    expect(entryLabel({ role: 'tool' })).toBe('Tool')
  })

  it('runs only the scrollback fallback through the terminal heuristics', () => {
    const items = toTranscriptItems({ source: 'scrollback', text: separated('real output', '*', '·', '✶', '✻') })

    expect(items).toEqual([{ role: 'terminal', label: 'Terminal', text: 'real output' }])
    expect(toTranscriptItems(null)).toEqual([])
  })
})

describe('toTranscriptBlocks', () => {
  it('forms base blocks, removes layout lines, and packs retained content', () => {
    const text = 'first line\n \n\t\nsecond line\n\nthird line'

    expect(toTranscriptBlocks(text)).toEqual([
      'first line\n\nsecond line\nthird line'
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

  it('removes known spinner and elapsed-token frames', () => {
    expect(toTranscriptBlocks(separated(
      '✢',
      '✣ · ✶',
      '✻50s · ↓ 1.0k tokens)',
      'meaningful terminal output'
    ))).toEqual(['meaningful terminal output'])
  })

  it('does not treat malformed token counters as elapsed frames', () => {
    expect(toTranscriptBlocks(separated(
      '✻50s · ↓ . tokens)',
      '✻50s · ↓ 1..2k tokens)',
      'meaningful terminal output'
    ))).toEqual([
      '✻50s · ↓ . tokens)\n\n✻50s · ↓ 1..2k tokens)\n\nmeaningful terminal output'
    ])
  })

  it('removes a run of four short redraw fragments', () => {
    expect(toTranscriptBlocks(separated(
      'l',
      'o',
      'g',
      'i',
      'meaningful terminal output'
    ))).toEqual(['meaningful terminal output'])
  })

  it('preserves up to three short blocks and isolated digits', () => {
    expect(toTranscriptBlocks(separated(
      'ls',
      '42',
      'OK',
      'meaningful terminal output'
    ))).toEqual([
      'ls\n\n42\n\nOK\n\nmeaningful terminal output'
    ])
  })

  it('counts astral Unicode characters as single code points at the short-block boundary', () => {
    expect(toTranscriptBlocks(separated(
      '😀'.repeat(12),
      'a',
      'b',
      'c',
      'meaningful terminal output'
    ))).toEqual(['meaningful terminal output'])

    expect(toTranscriptBlocks(separated(
      '😀'.repeat(13),
      'a',
      'b',
      'c',
      'meaningful terminal output'
    ))).toEqual([`${'😀'.repeat(13)}\n\na\n\nb\n\nc\n\nmeaningful terminal output`])
  })

  it('keeps the later whitespace-equivalent redraw', () => {
    expect(toTranscriptBlocks(separated(
      'first   screen state',
      ' first screen state '
    ))).toEqual([' first screen state '])
  })

  it('starts a new bubble after twenty source blocks', () => {
    const source = Array.from({ length: 21 }, (_, index) =>
      `meaningful source block ${String(index + 1).padStart(2, '0')}`)

    const result = toTranscriptBlocks(separated(...source))

    expect(result).toHaveLength(2)
    expect(result[0]).toContain('meaningful source block 01')
    expect(result[0]).toContain('meaningful source block 20')
    expect(result[0]).not.toContain('meaningful source block 21')
    expect(result[1]).toBe('meaningful source block 21')
  })

  it('keeps oversized source blocks intact and respects the size target', () => {
    const nearLimit = 'x'.repeat(1190)
    const oversized = 'y'.repeat(1201)

    expect(toTranscriptBlocks(separated(nearLimit, 'meaningful end block'))).toEqual([
      nearLimit,
      'meaningful end block'
    ])
    expect(toTranscriptBlocks(separated(oversized, 'meaningful end block'))).toEqual([
      oversized,
      'meaningful end block'
    ])
  })

  it('returns no blocks for missing, whitespace-only, or noise-only text', () => {
    expect(toTranscriptBlocks(null)).toEqual([])
    expect(toTranscriptBlocks(' \n\t\n')).toEqual([])
    expect(toTranscriptBlocks(separated('*', '·', '✶', '✻10s · ↓ 2k tokens)'))).toEqual([])
  })
})
