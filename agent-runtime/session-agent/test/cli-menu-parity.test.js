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
  assert.equal(detectMenu(screens.idle), null);
  assert.equal(detectMenu(screens.replyList), null);
});
