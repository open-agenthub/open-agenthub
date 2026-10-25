// Read only the live CLI composer. Prose, menus and ordinary drafts can contain the same text;
// Claude's dim cells AND cursor at the start distinguish a prediction from accepted input.
function claudeSuggestion(buffer, cols, rows) {
  const y = buffer.cursorY
  const line = offset => buffer.getLine(buffer.baseY + offset)
  const text = offset => line(offset)?.translateToString(true) || ''
  if (buffer.cursorX !== 2 || !/^❯\s/.test(text(y)) || !/^─{3}/.test(text(y - 1))) return ''
  const parts = []
  for (let row = y; row < rows; row++) {
    if (row > y && /^─{3}/.test(text(row))) {
      const suggestion = parts.join('').trimEnd()
      // The welcome screen's rotating examples share the prediction's dim styling.
      return suggestion && suggestion.length < 100 && !/^Try\s+["“]/.test(suggestion) ? suggestion : ''
    }
    const current = line(row)
    if (!current || (row > y && !/^\s{2}/.test(text(row)))) return ''
    const part = current.translateToString(true, 2, cols)
    for (let x = 2; x < 2 + part.length; x++) {
      const cell = current.getCell(x)
      if (cell?.getChars().trim() && !cell.isDim()) return ''
    }
    parts.push(part)
  }
  return ''
}

const readers = { claude: claudeSuggestion }

export function readPromptSuggestion(agent, terminal) {
  const reader = readers[String(agent || '').toLowerCase()]
  return reader && terminal ? reader(terminal.buffer.active, terminal.cols, terminal.rows) : ''
}
