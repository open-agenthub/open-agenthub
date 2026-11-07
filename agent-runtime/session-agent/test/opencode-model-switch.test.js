'use strict';

// OpenCode's switch walks its picker (#478): `/models`, the model's display name, Enter, and the
// TUI's own state file as the confirmation. The picker is a fake that writes what 1.18.35 writes.

const assert = require('node:assert/strict');
const test = require('node:test');

const { run, pickerName, stateFile } = require('../../opencode/model-switch');
const { ModelSwitchError } = require('../../common/model-switch');

const catalog = { models: [{ id: 'mock/mock-a', label: 'Mock A' }, { id: 'mock/mock-b', label: 'Mock B' }] };

function fakePicker({ picks = model => model, recentBefore = null } = {}) {
  const env = {};
  const home = '/home/agent';
  const file = stateFile(env, home);
  const files = recentBefore ? { [file]: JSON.stringify({ recent: [recentBefore] }) } : {};
  const fs = { readFileSync: name => { if (!(name in files)) throw new Error('ENOENT'); return files[name]; } };
  let open = false;
  let search = '';
  const sent = [];
  const tools = {
    env, home, fs,
    catalog: async () => catalog,
    type: async text => { sent.push(text); if (text === '/models') open = true; },
    keys: async data => {
      sent.push(data);
      if (!open) return;
      if (data === '\r') {
        const chosen = catalog.models.find(m => m.label === search);
        const id = picks(chosen && chosen.id);
        if (id) files[file] = JSON.stringify({ recent: [{ providerID: id.split('/')[0], modelID: id.split('/')[1] }] });
        open = false;
      } else if (data === '\x1b') open = false;
      else search += data;
    },
    screen: async () => open ? ['  Select model     esc', '  ' + search] : ['  Build · Mock A Mock']
  };
  let clock = 0;
  const timing = { now: () => (clock += 100), wait: async () => {} };
  return { tools, timing, sent };
}

const switchTo = (model, picker) => run(model, picker.tools,
  { ...picker.timing, ModelSwitchError, CONFIRM_TIMEOUT_MS: 15_000, POLL_MS: 150 });

test('the picker is opened bare, searched by display name and confirmed by the state file', async () => {
  const picker = fakePicker();
  assert.deepEqual(await switchTo('mock/mock-b', picker), { confirmation: 'OpenCode now uses Mock B' });
  // Never `/models <id>`: OpenCode sends that to the model as a prompt.
  assert.deepEqual(picker.sent, ['/models', ...'Mock B', '\r']);
});

test('a pick that landed on another model is reported as such', async () => {
  const picker = fakePicker({ picks: () => 'mock/mock-a' });
  await assert.rejects(switchTo('mock/mock-b', picker), { code: 'model_switch_wrong_model', detail: 'mock/mock-a' });
});

test('nothing picked closes the picker so the next message is not a search', async () => {
  const picker = fakePicker({ picks: () => null, recentBefore: { providerID: 'mock', modelID: 'mock-a' } });
  // The fake closes on Enter; one that stays open is closed with Escape.
  picker.tools.keys = (original => async data => { if (data === '\r') { picker.sent.push(data); return; } return original(data); })(picker.tools.keys);
  await assert.rejects(switchTo('mock/mock-b', picker), { code: 'model_switch_not_confirmed' });
  assert.equal(picker.sent.at(-1), '\x1b');
});

test('ids outside the catalog, without a provider, or with a name two rows share are refused before typing', async () => {
  const picker = fakePicker();
  await assert.rejects(switchTo('mock/nope', picker), { code: 'model_not_in_catalog' });
  await assert.rejects(switchTo('mock-b', picker), { code: 'invalid_model' });
  assert.throws(() => pickerName({ models: [{ id: 'a/x', label: 'GPT' }, { id: 'b/x', label: 'GPT' }] }, 'a/x', ModelSwitchError),
    { code: 'model_name_ambiguous' });
  assert.deepEqual(picker.sent, []);
});
