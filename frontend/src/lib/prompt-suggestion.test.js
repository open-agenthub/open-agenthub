import { expect, it } from 'vitest'
import { readPromptSuggestion } from './prompt-suggestion.js'
function screen({ prompt = 'run the tests', cursorX = 2, dim = true, prefix = '❯\u00a0', frame = true } = {}) {
  const lines = [frame ? '─'.repeat(120) : 'Earlier assistant output', prefix + prompt, '─'.repeat(120)]
  return { cols: 120, rows: 3, buffer: { active: { baseY: 0, cursorY: 1, cursorX,
    getLine: y => lines[y] === undefined ? undefined : ({
      translateToString: (trim, start = 0, end = 120) => lines[y].slice(start, end),
      getCell: x => ({ getChars: () => lines[y][x] || '', isDim: () => dim && y === 1 && x >= 2 })
    }) } } }
}
it('requires the dim live Claude prompt with the cursor at its beginning', () => {
  expect(readPromptSuggestion('Claude', screen())).toBe('run the tests')
  expect(readPromptSuggestion('Claude', screen({ cursorX: 15, dim: false }))).toBe('')
  expect(readPromptSuggestion('Claude', screen({ dim: false }))).toBe('')
  expect(readPromptSuggestion('Claude', screen({ frame: false }))).toBe('')
  expect(readPromptSuggestion('Claude', screen({ prefix: '● ' }))).toBe('')
})
it('rejects welcome examples and agents without a confirmed prediction source', () => {
  expect(readPromptSuggestion('Claude', screen({ prompt: 'run the tests and check that greeting hand…' }))).toBe('')
  expect(readPromptSuggestion('Claude', screen({ prompt: 'Try "edit a file"' }))).toBe('')
  expect(readPromptSuggestion('Codex', screen({ prefix: '› ' }))).toBe('')
  expect(readPromptSuggestion('OpenCode', screen())).toBe('')
})
