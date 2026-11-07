'use strict';

const path = require('node:path');

// OpenCode 1.18.35 has no command that takes a model: `/models <id>` is not a command at all and
// goes to the model as a prompt, with the id as the person's message. The bare `/models` opens its
// picker, whose search filters by the model's display name (an id such as "mock-b" matched
// nothing), and Enter takes the highlighted row. So the switch walks the picker: open it, type
// the name from the session's own catalog, Enter.
//
// What the TUI then uses is read from its own state file rather than off the screen: on every
// pick it writes the model first into `recent` of ~/.local/state/opencode/model.json, as
// provider and model id. That is exact where the footer ("Build · <name> <provider>") would
// confuse "Mock A" with "Mock A Pro".

const DIALOG = /^\s*Select model\b/;
const DIALOG_TIMEOUT_MS = 5_000;
const KEY_PAUSE_MS = 40;
const ESCAPE = '\x1b';

function stateFile(env, home) {
  const base = env.XDG_STATE_HOME || path.join(home, '.local', 'state');
  return path.join(base, 'opencode', 'model.json');
}

function lastPicked(fs, file) {
  try {
    const recent = JSON.parse(fs.readFileSync(file, 'utf8')).recent;
    const first = Array.isArray(recent) ? recent[0] : null;
    return first && typeof first.providerID === 'string' && typeof first.modelID === 'string'
      ? first.providerID + '/' + first.modelID : null;
  } catch {
    return null;
  }
}

/** The display name the picker shows for `model`, from the session's own catalog. */
function pickerName(catalog, model, ModelSwitchError) {
  const models = catalog && Array.isArray(catalog.models) ? catalog.models : [];
  const entry = models.find(candidate => candidate.id === model);
  if (!entry) throw new ModelSwitchError('model_not_in_catalog');
  const name = entry.label || model.slice(model.indexOf('/') + 1);
  // Two rows with one name (the same model under two providers) cannot be told apart by typing
  // the name; which one Enter takes is the picker's ranking, not the person's choice.
  if (models.filter(candidate => (candidate.label || candidate.id.slice(candidate.id.indexOf('/') + 1)) === name).length > 1)
    throw new ModelSwitchError('model_name_ambiguous', name);
  return name;
}

async function run(model, tools, { now, wait, ModelSwitchError, CONFIRM_TIMEOUT_MS, POLL_MS }) {
  if (!model.includes('/')) throw new ModelSwitchError('invalid_model');
  const name = pickerName(await tools.catalog(), model, ModelSwitchError);
  const file = stateFile(tools.env, tools.home);
  const before = lastPicked(tools.fs, file);

  await tools.type('/models');
  const shownBy = now() + DIALOG_TIMEOUT_MS;
  while (!(await tools.screen()).some(row => DIALOG.test(row))) {
    // Escape anyway: a picker that opens late would read the next typed message as its search.
    if (now() >= shownBy) { await tools.keys(ESCAPE); throw new ModelSwitchError('model_picker_not_shown'); }
    await wait(POLL_MS);
  }
  // Key by key: the search field is a plain input, and the picker filters on each key.
  for (const character of name) {
    await tools.keys(character);
    await wait(KEY_PAUSE_MS);
  }
  await wait(POLL_MS * 2);
  await tools.keys('\r');

  const deadline = now() + CONFIRM_TIMEOUT_MS;
  while (now() < deadline) {
    await wait(POLL_MS);
    const picked = lastPicked(tools.fs, file);
    // The picker closes on a pick. When the model was also the last one picked, the file alone
    // does not change, and only the closed picker says that Enter took it.
    const closed = !(await tools.screen()).some(row => DIALOG.test(row));
    if (picked === model && closed) return { confirmation: 'OpenCode now uses ' + name };
    // Another model was taken: the person needs to know, since that is now the model in use.
    if (picked !== before && picked !== null) throw new ModelSwitchError('model_switch_wrong_model', picked);
  }
  // Nothing was taken. A picker still open would read the next typed message as a search.
  if ((await tools.screen()).some(row => DIALOG.test(row))) await tools.keys(ESCAPE);
  throw new ModelSwitchError('model_switch_not_confirmed');
}

module.exports = { run, stateFile, lastPicked, pickerName, DIALOG };
