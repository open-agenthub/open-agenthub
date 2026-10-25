// Read only the live CLI composer. Prose, menus and ordinary drafts can contain the same text;
// Claude's dim cells AND cursor at the start distinguish a prediction from accepted input.
function claudeSuggestion(buffer, cols, rows) {
  const y = buffer.cursorY
  const line = offset => buffer.getLine(buffer.baseY + offset)
  const text = offset => line(offset)?.translateToString(true) || ''
  if (y < 1 || y + 1 >= rows || buffer.cursorX !== 2 || !/^❯\s/.test(text(y)) ||
      !/^─{3}/.test(text(y - 1)) || !/^─{3}/.test(text(y + 1))) return ''
  const current = line(y)
  const suggestion = current.translateToString(true, 2, cols)
  // The welcome examples share prediction styling. A narrow TUI truncates predictions with
  // an ellipsis: accepting that visible prefix would silently change the model's suggestion.
  if (!suggestion || suggestion.length >= 100 || /^Try\s+["“]/.test(suggestion) || /…$/.test(suggestion)) return ''
  for (let x = 2; x < cols; x++) {
    const cell = current.getCell(x)
    if (cell?.getChars().trim() && !cell.isDim()) return ''
  }
  return suggestion
}

const readers = { claude: claudeSuggestion }

export function readPromptSuggestion(agent, terminal) {
  const reader = readers[String(agent || '').toLowerCase()]
  return reader && terminal ? reader(terminal.buffer.active, terminal.cols, terminal.rows) : ''
}
