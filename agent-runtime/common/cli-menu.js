const { detectSignIn } = require('./cli-sign-in');
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
// How far up from the bottom a menu is looked for. A TUI draws its dialog at the bottom; a
// numbered list higher up is the agent's earlier answer scrolling by.
const WINDOW = 24

/**
 * @param {string[]} lines the visible terminal rows, top to bottom
 * @returns {null | { kind: 'choice' | 'input', title: string, options: { number: number, label: string, selected: boolean }[] }}
 */
function detectMenu(lines) {
  const signIn = detectSignIn(lines)
  if (signIn) return signIn
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
    options.push({ number, label: match[3].replace(/\s{2,}/g, ' · '), selected: Boolean(match[1]) })
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

// The heading of the dialog. A line ending in a colon or question mark right above the choices
// asks the question ("Select login method:", "How do you want to sign in?"); otherwise the topmost short line inside the dialog's frame names it
// ("Select model", "Login") — the lines between are its explanation.
function titleAbove(region, index) {
  let nearest = ''
  let heading = ''
  for (let i = index - 1; i >= 0 && i >= index - 8; i--) {
    const text = region[i].trim().replace(/\s{2,}/g, ' ')
    if (/^[─━▔▁═_-]{3,}/.test(text)) break
    if (!text || !/[A-Za-z]/.test(text) || /^https?:\/\//.test(text)) continue
    if (/[:?]$/.test(text)) return text
    nearest ||= text
    if (text.length <= 40) heading = text
  }
  return heading || nearest
}

module.exports = { detectMenu };
