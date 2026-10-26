'use strict';

// common/cli-menu.js is a copy of the Workspace's detector (frontend/src/lib/cli-menu.js): the
// session agent must call the same screens a menu as the Workspace does, or a fleet message could
// be typed into a dialog the person is being shown as one (#173). Both run over the same screens.
const assert = require('node:assert/strict');
const path = require('node:path');
const { pathToFileURL } = require('node:url');
const test = require('node:test');

const { detectMenu } = require('../../common/cli-menu');

const screens = {
  modelPicker: ['  Select Model and Effort', '› 1. Model One (default)  Latest model.', '  2. Model Two            Older model.', '', '  enter select · esc back'],
  approval: ['Would you like to run the following command?', '', '› 1. Yes, proceed (y)', '  2. Yes, and don\'t ask again (p)', '  3. No, and tell the agent what to do differently (esc)', '', '  Press enter to confirm or esc to cancel'],
  loginCode: ['Login', 'Open the link below and sign in', 'https://example.com/device', 'Paste code here if prompted >', 'Esc to cancel'],
  question: ['─'.repeat(90), ' ☐ Target ', '', '│ Pick the deploy target for tonight. Note the database migration from yesterday must run ', '│ first', '', '❯ 1. Staging', '     x', '  2. Production', '  3. Type something.', 'Enter to select · ↑/↓ to navigate · Esc to cancel'],
  wrapped: ['─'.repeat(90), '  Deploy', '  The pipeline runs every image build on each push, which takes about forty minutes and', '  blocks the merge queue. Should I split it?', '  ❯ 1. Split it', '    2. Keep it', '  Esc to cancel'],
  codexQuestion: ["  Question 1/1 (1 unanswered)","  Before I refactor the upload path I need to know how you want failures handled. The current code retries three","  times with a fixed delay, swallows the last error and marks the file as uploaded anyway, which is why the Files pane","  sometimes shows files that never reached storage. Which behaviour do you want?","","  › 1. Retry with backoff (Recommended)  Retry five times with exponential backoff, then surface the error","    2. Fail fast                         Report the first error to the user immediately","    3. None of the above                 Optionally, add details in notes (tab)","","  tab to add notes | enter to submit answer | esc to interrupt"],
  openCodeMulti: ["  ┃  Which checks should run before every push? (select all that apply)","  ┃","  ┃  1. [✓] Unit tests","  ┃     The fast suite","  ┃  2. [ ] Lint","  ┃     ESLint and dotnet format","  ┃  3. [ ] Type your own answer","  ┃","  ┃  ⇆ tab  ↑↓ select  enter toggle  esc dismiss"],
  openCodeReview: ["  ┃   Checks   Confirm","  ┃","  ┃  Review","  ┃","  ┃  Checks: Unit tests, Lint","  ┃","  ┃  ⇆ tab  enter submit  esc dismiss"],
  claudeMultiOnSubmit: ["←  ☒ Checks  ✔ Submit  →","","Which checks should run before every push?","","  1. [✔] Unit tests","         The fast suite","  2. [ ] Lint","         ESLint and dotnet format","  3. [ ] Type something","❯    Submit","────────────────────────────────────────────────────────────────────────────────────────────────────","  4. Chat about this","","Enter to select · ↑/↓ to navigate · Esc to cancel"],
  clippedList: ['Choose:', '❯ 4. Fourth', 'Enter to confirm · Esc to cancel'],
  replyList: ['• Here is the plan:', '  1. Read the code', '  2. Fix the bug', '  3. Run the tests', '', '› Ask the agent to do anything'],
  idle: ['› Ask the agent to do anything', '', '  model · ~/work'],
  empty: []
};

test('the session agent and the Workspace agree on every screen', async () => {
  const frontend = await import(pathToFileURL(path.join(__dirname, '..', '..', '..', 'frontend', 'src', 'lib', 'cli-menu.js')).href);
  for (const [name, lines] of Object.entries(screens)) {
    assert.deepEqual(detectMenu(lines), frontend.detectMenu(lines), name);
  }
  assert.ok(detectMenu(screens.approval), 'an approval dialog is a menu');
  // #284: the session agent must not type a fleet message into OpenCode's question or review.
  assert.ok(detectMenu(screens.openCodeMulti) && detectMenu(screens.openCodeReview));
  assert.equal(detectMenu(screens.idle), null);
  assert.equal(detectMenu(screens.replyList), null);
});
