import { describe, expect, it } from 'vitest'
import { detectMenu, keysForAnswer, keysForOption, keysForStep, sameMenu } from './cli-menu.js'

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

// AskUserQuestion screens of the same CLI (#250), blank rows included, the question and options as the mock model
// sent them. A long question is framed in a bar and wrapped; "Type something." is the entry that
// becomes a text field.
const question = [
  '❯ ask me',
  rule,
  ' ☐ Failures ',
  '',
  '│ Before I refactor the upload path I need to know how you want failures handled. The current code retries three times ',
  '│ with a fixed delay, swallows the last error and marks the file as uploaded anyway, which is why the Files pane ',
  '│ sometimes shows files that never reached storage. Which behaviour do you want?',
  '',
  '❯ 1. Retry with backoff',
  '     Retry five times with exponential backoff, then surface the error',
  '  2. Fail fast',
  '     Report the first error to the user immediately',
  '  3. Type something.',
  rule,
  '  4. Chat about this',
  '',
  'Enter to select · ↑/↓ to navigate · Esc to cancel'
]
const statement = [
  rule.slice(0, 90),
  ' ☐ Target ',
  '',
  '│ Pick the deploy target for tonight. Note the database migration from yesterday must run ',
  '│ first',
  '',
  '❯ 1. Staging', '     x', '  2. Production', '     y', '  3. Type something.',
  rule.slice(0, 90), '  4. Chat about this', 'Enter to select · ↑/↓ to navigate · Esc to cancel'
]
const multiSelect = [
  rule.slice(0, 90),
  '←  ☐ Checks  ☐ Report  ✔ Submit  →',
  'Which checks should run before every push?',
  '',
  '❯ 1. [ ] Unit tests', '         The fast suite', '  2. [ ] Lint', '         ESLint and dotnet format',
  '  3. [ ] Type something', '     Next',
  rule.slice(0, 90), '  4. Chat about this', 'Enter to select · Tab/Arrow keys to navigate · Esc to cancel'
]

// #284: the question screens of the other pinned CLIs and Claude Code's multi-select step, as
// rendered by xterm 5.5 from the real CLIs (Codex 0.160.0 in Plan mode answering request_user_input,
// OpenCode 1.18.35 answering its question tool), rows and blank rows as the screen had them.
const codexQuestion = [
  "  Question 1/1 (1 unanswered)",
  "  Before I refactor the upload path I need to know how you want failures handled. The current code retries three",
  "  times with a fixed delay, swallows the last error and marks the file as uploaded anyway, which is why the Files pane",
  "  sometimes shows files that never reached storage. Which behaviour do you want?",
  "",
  "  › 1. Retry with backoff (Recommended)  Retry five times with exponential backoff, then surface the error",
  "    2. Fail fast                         Report the first error to the user immediately",
  "    3. None of the above                 Optionally, add details in notes (tab)",
  "",
  "  tab to add notes | enter to submit answer | esc to interrupt"
]
const openCodeQuestion = [
  "  ┃  Before I refactor the upload path I need to know how you want failures handled. The current code retries",
  "  ┃  three times with a fixed delay, swallows the last error and marks the file as uploaded anyway, which is why",
  "  ┃  the Files pane sometimes shows files that never reached storage. Which behaviour do you want?",
  "  ┃",
  "  ┃  1. Retry with backoff",
  "  ┃     Retry five times with exponential backoff, then surface the error",
  "  ┃  2. Fail fast",
  "  ┃     Report the first error to the user immediately",
  "  ┃  3. Type your own answer",
  "  ┃",
  "  ┃  ↑↓ select  enter submit  esc dismiss"
]
const openCodeMulti = [
  "  ┃  Which checks should run before every push? (select all that apply)",
  "  ┃",
  "  ┃  1. [✓] Unit tests",
  "  ┃     The fast suite",
  "  ┃  2. [ ] Lint",
  "  ┃     ESLint and dotnet format",
  "  ┃  3. [ ] Type your own answer",
  "  ┃",
  "  ┃  ⇆ tab  ↑↓ select  enter toggle  esc dismiss"
]
const openCodeReview = [
  "  ┃   Checks   Confirm",
  "  ┃",
  "  ┃  Review",
  "  ┃",
  "  ┃  Checks: Unit tests, Lint",
  "  ┃",
  "  ┃  ⇆ tab  enter submit  esc dismiss"
]
const claudeMultiOnSubmit = [
  "←  ☒ Checks  ✔ Submit  →",
  "",
  "Which checks should run before every push?",
  "",
  "  1. [✔] Unit tests",
  "         The fast suite",
  "  2. [ ] Lint",
  "         ESLint and dotnet format",
  "  3. [ ] Type something",
  "❯    Submit",
  "────────────────────────────────────────────────────────────────────────────────────────────────────",
  "  4. Chat about this",
  "",
  "Enter to select · ↑/↓ to navigate · Esc to cancel"
]
const longQuestion = 'Before I refactor the upload path I need to know how you want failures handled. The current code retries three '
  + 'times with a fixed delay, swallows the last error and marks the file as uploaded anyway, which is why the Files pane '
  + 'sometimes shows files that never reached storage. Which behaviour do you want?'

describe('question screens of Codex, OpenCode and Claude multi-select (#284)', () => {
  it('reads Codex\'s question whole, and its "None of the above" as a note opened with Tab', () => {
    const menu = detectMenu(codexQuestion)
    expect(menu.title).toBe(longQuestion)
    expect(menu.options.map(option => [option.number, option.selected, option.freeText ?? false])).toEqual([[1, true, false], [2, false, false], [3, false, true]])
    expect(menu.options[2].opensWith).toBe('\t')
    expect(keysForAnswer(menu, 3, 'Retry 3 times\nthen tell me')).toEqual(['\x1b[B', '\x1b[B', '\t', '\x1b[200~Retry 3 times\nthen tell me\x1b[201~', '\r'])
  })

  it('reads OpenCode\'s framed question, which shows no cursor, and answers it by digit', () => {
    const menu = detectMenu(openCodeQuestion)
    expect(menu.kind).toBe('choice')
    expect(menu.title.replace(/ +/g, ' ')).toBe(longQuestion.replace(/ +/g, ' '))
    expect(menu.options.map(option => option.label)).toEqual(['Retry with backoff', 'Fail fast', 'Type your own answer'])
    expect(menu.options.some(option => option.selected)).toBe(false)
    expect(keysForOption(menu, 2)).toEqual(['2'])
    expect(keysForAnswer(menu, 3, 'my own words')).toEqual(['3', '\x1b[200~my own words\x1b[201~', '\r'])
  })

  it('offers OpenCode\'s multi-select Tab step and then its review to submit', () => {
    const menu = detectMenu(openCodeMulti)
    expect(menu.title).toBe('Which checks should run before every push? (select all that apply)')
    expect(menu.step).toEqual({ label: 'Next', via: 'tab' })
    expect(keysForOption(menu, 2)).toEqual(['2'])
    expect(keysForAnswer(menu, 3, 'e2e too')).toEqual(['3', '\x1b[200~e2e too\x1b[201~', '\r'])
    expect(keysForStep(menu)).toEqual(['\t'])
    expect(detectMenu(openCodeReview)).toEqual({ kind: 'confirm', title: 'Review your answers', options: [], details: ['Checks: Unit tests, Lint'] })
  })

  it('counts Claude Code\'s Submit row when moving, wherever the cursor is', () => {
    const menu = detectMenu(claudeMultiOnSubmit)
    expect(menu.step).toEqual({ label: 'Submit', selected: true, after: 3 })
    expect(keysForStep(menu)).toEqual(['\r'])
    expect(keysForOption(menu, 1)).toEqual(['\x1b[A', '\x1b[A', '\x1b[A', '\r'])
    expect(keysForOption(menu, 4)).toEqual(['\x1b[B', '\r'])
    const onFirst = { ...menu, step: { ...menu.step, selected: false }, options: menu.options.map(option => ({ ...option, selected: option.number === 1 })) }
    expect(keysForStep(onFirst)).toEqual(['\x1b[B', '\x1b[B', '\x1b[B', '\r'])
    // Before #284 this was three arrows, which stopped on Submit instead of "4. Chat about this".
    expect(keysForOption(onFirst, 4)).toEqual(['\x1b[B', '\x1b[B', '\x1b[B', '\x1b[B', '\r'])
  })
})

describe('detectMenu', () => {
  it('takes the whole of a long question the CLI framed and wrapped, and marks the free-text entry', () => {
    expect(detectMenu(question)).toEqual({
      kind: 'choice', partial: false,
      title: 'Before I refactor the upload path I need to know how you want failures handled. The current code retries three times '
        + 'with a fixed delay, swallows the last error and marks the file as uploaded anyway, which is why the Files pane '
        + 'sometimes shows files that never reached storage. Which behaviour do you want?',
      options: [
        { number: 1, label: 'Retry with backoff', selected: true },
        { number: 2, label: 'Fail fast', selected: false },
        { number: 3, label: 'Type something.', selected: false, freeText: true },
        { number: 4, label: 'Chat about this', selected: false }
      ]
    })
    // Without a question mark the framed text is still the question, not the header above it.
    expect(detectMenu(statement).title).toBe('Pick the deploy target for tonight. Note the database migration from yesterday must run first')
    const multi = detectMenu(multiSelect)
    expect(multi.title).toBe('Which checks should run before every push?')
    expect(multi.options[2]).toEqual({ number: 3, label: '[ ] Type something', selected: false, freeText: true })
  })

  it('joins a question the CLI wrapped without a frame, but not the heading or explanation above it', () => {
    const wrapped = [rule,
      '   Deploy',
      '   The deploy pipeline currently runs every image build on each push, which takes about forty minutes and blocks',
      '   the merge queue for everyone else. Should I split it so that only the images whose sources changed are built?',
      '   ❯ 1. Split it', '     2. Keep it', '   Esc to cancel']
    expect(detectMenu(wrapped).title).toBe('The deploy pipeline currently runs every image build on each push, which takes about forty minutes and blocks '
      + 'the merge queue for everyone else. Should I split it so that only the images whose sources changed are built?')
  })


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

describe('keysForAnswer', () => {
  it('moves to the free-text entry, pastes the answer and submits it', () => {
    expect(keysForAnswer(detectMenu(question), 3, 'Retry 3 times\nthen \x1bstop')).toEqual(
      ['\x1b[B', '\x1b[B', '\x1b[200~Retry 3 times\nthen stop\x1b[201~', '\r'])
  })

  it('leaves a multi-select answer ticked rather than pressing Enter, which would untick it', () => {
    expect(keysForAnswer(detectMenu(multiSelect), 3, 'e2e')).toEqual(['\x1b[B', '\x1b[B', '\x1b[200~e2e\x1b[201~'])
  })
})

describe('sameMenu', () => {
  it('compares by content', () => {
    expect(sameMenu(detectMenu(login), detectMenu([...login]))).toBe(true)
    expect(sameMenu(detectMenu(login), detectMenu(model))).toBe(false)
    expect(sameMenu(null, null)).toBe(true)
  })
})
