'use strict';

// Codex 0.165.0 removed the `worktrees` feature flag that kept /new from asking "Where should the
// new conversation run?" in a git checkout, and that menu swallowed the context-clear handover
// (#336). The driver clears with /clear, which never asks. The screens in
// fixtures/codex-clear-screens.json are the real ones, rendered from Codex 0.160.0 and 0.165.0 in a
// git checkout under the managed requirements, after a prompt and then the command.
const test = require('node:test');
const assert = require('node:assert/strict');
const { Terminal } = require('@xterm/headless');

const screens = require('./fixtures/codex-clear-screens.json');
const driver = require('../../codex/driver');
const { detectMenu } = require('../../common/cli-menu');
const { createNextTurnDelivery } = require('../../common/next-turn');
const { createContextClear } = require('../../common/context-clear');

const SIZE = { cols: 120, rows: 32 };

test('Codex clears with /clear, whose screen is the fresh composer on both versions', () => {
  assert.equal(driver.contextClearCommand, '/clear');
  for (const name of ['codex-0.160.0 /clear', 'codex-0.165.0 /clear']) {
    assert.equal(detectMenu(screens[name]), null, name);
    assert.ok(driver.contextClearScreen(screens[name]), name);
  }
});

test('the worktree picker of both versions is a menu and never a cleared screen', () => {
  for (const name of ['codex-0.160.0 /new', 'codex-0.165.0 /new']) {
    assert.ok(detectMenu(screens[name]), name);
    assert.equal(driver.contextClearScreen(screens[name]), false, name);
  }
});

// Should a picker ever stand where the fresh composer belongs, the handover must not be typed
// into it: the readiness wait refuses, and the error and retry path keeps the handover saved.
test('nothing is typed while the real 0.165.0 picker is on the screen', async () => {
  let clock = 0;
  const delivery = createNextTurnDelivery({
    now: () => clock,
    callback: 'http://hub.example.com/internal/sessions/s1',
    token: 'tok',
    wait: async ms => { clock += ms; },
    log: () => {},
    Terminal: () => Terminal,
    fetchImpl: async () => ({ ok: true, json: async () => ({ messages: [] }) })
  });
  const writes = [];
  const term = { write: data => writes.push(data) };
  const picker = screens['codex-0.165.0 /new'].join('\r\n');
  const queue = [];
  const errors = [];
  let saved = null;
  const clear = createContextClear({
    store: { load: () => saved, save: state => { saved = state; }, remove: () => { saved = null; } },
    scheduleAtSafePoint: action => queue.push(action),
    clear: async () => { term.write(driver.contextClearCommand); term.write('\r'); return 'old'; },
    waitForClear: () => delivery.waitForInputReady(term, () => picker, SIZE, driver.contextClearScreen),
    waitForInput: () => delivery.waitForInputReady(term, () => picker, SIZE),
    deliver: text => delivery.typeHandover(term, text, () => picker, SIZE),
    verify: async () => null,
    onError: (error, info) => errors.push({ message: error.message, ...info }),
    onDelivered: () => assert.fail('delivered into the picker'),
    log: () => {}
  });

  clear.schedule('State: the patch is written. Open task: run the suite.');
  await queue.shift()();

  assert.deepEqual(writes, ['/clear', '\r']);
  assert.equal(errors[0].message, 'context_clear_input_not_ready');
  assert.equal(errors[0].retry, true);
  assert.ok(saved, 'the handover stays saved for the next attempt');
});
