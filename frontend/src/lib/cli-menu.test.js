import { describe, expect, it } from 'vitest'
import { detectMenu, keysForOption, sameMenu } from './cli-menu.js'

// Screens as Claude Code 2.1.287 drew them at 120x40, rendered by xterm 5.5 (the frontend's
// version) from the real CLI's output. Option texts are shortened to neutral labels; layout,
// glyphs, indentation and footers are the CLI's own.
const rule = '─'.repeat(120)
const idle = [
  ' ▐▛███▛█   Claude Code v2.1.287',
  '▝▜██████▀  Opus 5.5 · API Usage Billing',
  '❯ /model',
  '  ⎿  Set model to Sonnet 5.5 and saved as your default for new sessions',
  ' '.repeat(100) + '◐ medium · /effort',
  rule, '❯ ', rule,
  '  ⏵⏵ auto mode on (shift+tab to cycle) · ← for agents'
]
const login = [
  '▝▜██████▀  Opus 5.5 · API Usage Billing',
  '▔'.repeat(120),
  '   Login',
  '   Claude Code can be used with your Claude subscription or billed based on API usage through your Console account.',
  '   Select login method:',
  '   ❯ 1. Claude account with subscription · Pro, Max, Team, or Enterprise',
  '     2. Console account · API usage billing',
  '     3. 3rd-party platform · cloud providers',
  '   Esc to cancel',
  '', ''
]
const model = [
  '▔'.repeat(90) + ' ◐ medium · /effort ▔',
  '   Select model',
  '   Switch between Claude models. Your pick becomes the default for new sessions. For other/previous model names, ',
  '   specify with --model.',
  '     1. Default (recommended) ✔  Use the default model · $4/$20 per Mtok',
  '     2. Opus                     Opus 5.5 · Best for everyday, complex tasks · $4/$20 per Mtok',
  '     3. Fable                    Fable 5.1 · Most capable for your hardest and longest-running tasks · $10/$50 per ',
  '                                 Mtok',
  '   ❯ 4. Sonnet                   Sonnet 5.5 · Efficient for routine tasks · $2/$10 per Mtok',
  '     5. Haiku                    Haiku 4.5 · Fastest for quick answers · $1/$5 per Mtok',
  '   ◐ Medium effort (default) ←/→ to adjust',
  '   Enter to set as default · s to use this session only · Esc to cancel'
]
const oauth = [
  '▔'.repeat(120),
  '   Login',
  "   Browser didn't open? Use the url below to sign in (c to copy)",
  'https://example.com/oauth/authorize?code=true&client_id=x&response_type=code',
  "   Hold Shift while selecting to use your terminal's native copy",
  '   Paste code here if prompted >',
  '   Esc to cancel'
]

describe('detectMenu', () => {
  it('finds the login menu with its title, options and the highlighted one', () => {
    expect(detectMenu(login)).toEqual({
      kind: 'choice', title: 'Select login method:', partial: false,
      options: [
        { number: 1, label: 'Claude account with subscription · Pro, Max, Team, or Enterprise', selected: true },
        { number: 2, label: 'Console account · API usage billing', selected: false },
        { number: 3, label: '3rd-party platform · cloud providers', selected: false }
      ]
    })
  })

  it('reads a column menu whose cursor moved, joining the padded columns', () => {
    const menu = detectMenu(model)
    expect(menu.kind).toBe('choice')
    expect(menu.title).toBe('Select model')
    expect(menu.options.map(option => option.number)).toEqual([1, 2, 3, 4, 5])
    expect(menu.options[3]).toEqual({ number: 4, label: 'Sonnet · Sonnet 5.5 · Efficient for routine tasks · $2/$10 per Mtok', selected: true })
  })

  it('takes a question right above the choices as the title', () => {
    const submenu = ['▔'.repeat(120), '   Login', '   Console account', '   How do you want to sign in?   ',
      '   ❯ 1. Sign in with your Console account (recommended)', '     2. Create an API key (legacy)', '     3. Go back', '   Esc to cancel']
    expect(detectMenu(submenu).title).toBe('How do you want to sign in?')
  })

  it('reads a list the CLI scrolled down to its highlighted entry in a short terminal as partial', () => {
    // The same login menu at 14 rows, as the browser's drawer gave it to the real CLI.
    const short = ['   Login', '   Select login method:',
      '   ❯ 1. Claude account with subscription · Pro, Max, Team, or Enterprise', '   Esc to cancel']
    expect(detectMenu(short)).toMatchObject({ kind: 'choice', partial: true, options: [{ number: 1, selected: true }] })
    // Without the footer a lone highlighted line is the CLI's own input prompt, not a menu.
    expect(detectMenu(['❯ 1. remember to run the tests'])).toBeNull()
  })

  it('recognizes the CLI authorization-code screen', () => {
    expect(detectMenu(oauth)).toMatchObject({ kind: 'signin', mode: 'code', status: 'waiting', url: 'https://example.com/oauth/authorize?code=true&client_id=x&response_type=code' })
  })

  it('stays quiet at the normal prompt', () => {
    expect(detectMenu(idle)).toBeNull()
  })

  it('does not take a numbered list in the agent reply for a menu', () => {
    const reply = [
      '● Here is the plan:',
      '  1. Write the failing test',
      '  2. Fix the parser',
      '  3. Run the suite',
      rule, '❯ ', rule,
      '  ⏵⏵ auto mode on (shift+tab to cycle) · esc to interrupt'
    ]
    expect(detectMenu(reply)).toBeNull()
  })

  it('finds a footer-only menu without a cursor glyph, and a yes/no prompt', () => {
    expect(detectMenu(['Allow this command?', '  1. Yes', '  2. No', 'Press enter to confirm or esc to go back'])?.options).toHaveLength(2)
    expect(detectMenu(['Trust this folder?', '› 1. Yes, continue', '  2. No, quit'])?.kind).toBe('choice')
  })

  it('ignores a menu that scrolled far above the bottom of the screen', () => {
    const old = [...login, ...Array(30).fill('output line'), ...idle]
    expect(detectMenu(old)).toBeNull()
  })
})

describe('keysForOption', () => {
  const menu = detectMenu(model)
  it('moves from the highlighted option with arrows and confirms with Enter', () => {
    expect(keysForOption(menu, 4)).toEqual(['\r'])
    expect(keysForOption(menu, 5)).toEqual(['\x1b[B', '\r'])
    expect(keysForOption(menu, 1)).toEqual(['\x1b[A', '\x1b[A', '\x1b[A', '\r'])
  })
})

describe('sameMenu', () => {
  it('compares by content', () => {
    expect(sameMenu(detectMenu(login), detectMenu([...login]))).toBe(true)
    expect(sameMenu(detectMenu(login), detectMenu(model))).toBe(false)
    expect(sameMenu(null, null)).toBe(true)
  })
})
