'use strict';

// The Workspace's CLI menu detector (frontend/src/lib/cli-menu.js, #112/#166), for the session
// agent: before it types a fleet message into an idle agent it has to know the CLI is not showing
// one of its own screens, where the text would be read as a choice (docs/priority-messages.md).
// A copy rather than a shared module because the runtime images are built without the frontend;
// session-agent/test/cli-menu-parity.test.js runs both over the same screens so they cannot drift.
// Only detectMenu and what it needs are here — the key helpers are the Workspace's business.

// A numbered option, with or without the cursor in front of it: "❯ 1. Label", "  2. Label",
// "› 3) Label". The cursor glyphs are the ones the agent CLIs draw for a highlighted entry.
const OPTION = /^\s*([❯›>▶➤→])?\s*(\d{1,2})[.)]\s+(\S.*?)\s*$/
// Footers a CLI prints under an interactive screen and never under normal output.
const FOOTER = /\b(?:esc to (?:cancel|go back|close|exit)|enter to (?:confirm|select|continue|set)|press enter to (?:confirm|continue|select)|↑\/↓ to (?:select|navigate|move)|use arrow keys)\b|\((?:y\/n|Y\/n|y\/N)\)|\[(?:y\/n|Y\/n|y\/N)\]/i
// A prompt for typed input inside such a screen, such as the code a login asks to be pasted.
const INPUT = /\b(?:paste (?:the )?code|enter (?:the )?code|paste .* here)\b.*[>:]\s*$/i
// The entry Claude Code adds to every AskUserQuestion list (#250): picking it turns the row into a
// text field whose content becomes the answer, so it is not a choice like the others. A
// multi-select question draws it with its checkbox, "[ ] Type something".
const FREE_TEXT = /^(?:\[[ ✔]\]\s*)?type something\.?$/i
// Claude Code frames a long question in a bar ("│ …") and wraps it; every line of it is the question.
const QUOTED = /^\s*│\s?/
// The frame a CLI draws above and below its dialog.
const RULE = /^[─━▔▁═_-]{3,}/
// How far up from the bottom a menu is looked for. A TUI draws its dialog at the bottom; a
// numbered list higher up is the agent's earlier answer scrolling by.
const WINDOW = 24

/**
 * @param {string[]} lines the visible terminal rows, top to bottom
 * @returns {null | { kind: 'choice' | 'input', title: string, options: { number: number, label: string, selected: boolean, freeText?: true }[] }}
 */
function detectMenu(lines) {
  const rows = (lines || []).map(line => String(line ?? '').replace(/\s+$/, ''))
  let end = rows.length
  while (end > 0 && !rows[end - 1].trim()) end -= 1
  const start = Math.max(0, end - WINDOW)
  const region = rows.slice(start, end)
  const footer = region.findLastIndex(line => FOOTER.test(line))

  const options = []
  let first = -1
  region.forEach((line, index) => {
    const match = OPTION.exec(line)
    if (!match) return
    const number = Number(match[2])
    // Only a run counting up from where it began is one menu; a "1." in the agent's prose
    // further down starts nothing.
    if (options.length && number !== options[options.length - 1].number + 1) {
      if (number !== 1) return
      options.length = 0
    }
    if (!options.length) first = index
    // Columns ("Opus      Opus 5.5 · …") are padded with spaces; one separator reads the same.
    const label = match[3].replace(/\s{2,}/g, ' · ')
    options.push({ number, label, selected: Boolean(match[1]), ...(FREE_TEXT.test(label) ? { freeText: true } : {}) })
  })

  const hasCursor = options.some(option => option.selected)
  const footed = footer > first && first >= 0
  // Both conditions guard against the same false alarm: a numbered list in the agent's reply.
  // A list alone is prose; a list with the CLI's cursor in it, or above its footer, is a menu.
  // A single entry counts only with both: a CLI given few rows scrolls its list down to the
  // highlighted entry (Claude Code shows one option in a 14-row terminal), and then the
  // visible options are not all there are — `partial` says so.
  if ((options.length >= 2 && (hasCursor || footed)) || (options.length === 1 && hasCursor && footed)) {
    return { kind: 'choice', title: titleAbove(region, first), options, partial: options.length === 1 || options[0].number !== 1 }
  }
  const input = region.findLastIndex(line => INPUT.test(line))
  if (input >= 0 && footer >= input) return { kind: 'input', title: titleAbove(region, input), options: [] }
  return null
}

// The heading of the dialog. A question Claude Code framed in its bar is taken whole, however many
// lines it wraps to. Otherwise a line ending in a colon or question mark right above the choices
// asks the question ("Select login method:", "How do you want to sign in?"), together with the
// lines it wrapped from; failing that, the topmost short line inside the dialog's frame names it
// ("Select model", "Login") — the lines between are its explanation. Reading only the last line
// of a wrapped question left the person answering half of it.
function titleAbove(region, index) {
  const quoted = []
  let top = index - 1
  while (top >= 0 && !region[top].trim()) top--
  for (let i = top; i >= 0 && QUOTED.test(region[i]); i--) quoted.unshift(region[i].replace(QUOTED, '').trim())
  if (quoted.length) return quoted.filter(Boolean).join(' ')

  const width = Math.max(...region.map(line => line.length))
  let nearest = ''
  let heading = ''
  for (let i = index - 1; i >= 0 && i >= index - 8; i--) {
    const text = region[i].trim().replace(/\s{2,}/g, ' ')
    if (RULE.test(text)) break
    if (!text || !/[A-Za-z]/.test(text) || /^https?:\/\//.test(text)) continue
    if (/[:?]$/.test(text)) return [...wrappedAbove(region, i, width), text].join(' ')
    nearest ||= text
    if (text.length <= 40) heading = text
  }
  return heading || nearest
}

// The lines a question at `index` continues: each one reaches nearly across the screen, as a
// line the CLI broke for width does, and does not end a sentence. A short line above is a
// heading of its own ("Console account"), a full one ending in a full stop the explanation.
function wrappedAbove(region, index, width) {
  const lines = []
  for (let i = index - 1; i >= 0; i--) {
    const text = region[i].trim()
    if (!text || RULE.test(text) || region[i].length < width * 0.75 || /[.!?:]$/.test(text) || OPTION.test(region[i]) || FOOTER.test(region[i])) break
    lines.unshift(text.replace(/\s{2,}/g, ' '))
  }
  return lines
}

module.exports = { detectMenu };
