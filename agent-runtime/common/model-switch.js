'use strict';

// Switches the model of the running CLI from the Workspace (#478, docs/design/live-model-switch.md).
// Every pinned CLI that can do it has its own command, and its own way of saying it did; each
// driver describes both in `modelSwitch`, and this module types the command, answers the one
// dialog a driver names, and waits for the CLI's own confirmation. The switch counts as done
// only when the CLI said so: a command typed and nothing seen afterwards is an error, not a
// success the Workspace would show as the new model.

const { MODEL_ID } = require('./model-catalog');

// Measured on the pinned CLIs: Claude Code answers in under a second once its "Switch model?"
// dialog is answered, Codex and OpenClaw at once, OpenCode's picker within two. Claude Code
// checks the model with a request to the provider first, which a slow provider can stretch.
const CONFIRM_TIMEOUT_MS = 15_000;
const POLL_MS = 200;
// How much of the output before the switch is replayed under what follows it. A confirmation is
// new output, so it is near the end; the bound keeps a long session's scrollback from being
// rendered on every poll. The same prefix is used for the count before and after, so a line
// cannot drop out of the window and hide a new confirmation behind it.
const RENDER_BYTES = 256 * 1024;

class ModelSwitchError extends Error {
  constructor(code, detail) {
    super(code);
    this.code = code;
    if (detail) this.detail = detail;
  }
}

function validateModel(value) {
  if (typeof value !== 'string' || !MODEL_ID.test(value)) throw new ModelSwitchError('invalid_model');
  return value;
}

function escapeRegExp(text) {
  return text.replace(/[.*+?^${}()|[\]\\]/g, '\\$&');
}

/** Every line of a terminal that printed `output`, scrollback included, top to bottom. */
function renderLines(output, size, Terminal) {
  return new Promise(resolve => {
    const terminal = new Terminal({ cols: size.cols, rows: size.rows, allowProposedApi: true, scrollback: 5000 });
    terminal.write(output, () => {
      const buffer = terminal.buffer.active;
      const lines = [];
      for (let i = 0; i < buffer.length; i++) {
        const line = buffer.getLine(i);
        lines.push(line ? line.translateToString(true) : '');
      }
      terminal.dispose();
      resolve(lines);
    });
  });
}

function matching(lines, pattern) {
  return lines.filter(line => pattern.test(line));
}

/**
 * Types the driver's command and waits for its confirmation. A confirmation counts only when the
 * rendered output holds more matching lines than before the command: switching back to a model
 * used earlier in the session shows the same line that is still in the scrollback.
 *
 * tools: type(text) types a command and presses Enter; keys(data) writes raw keys; lines() renders
 * the output since `before` was taken, on top of it; before() the lines as the switch started;
 * screen() the visible rows.
 */
async function runCommandPlan(plan, model, tools, { now, wait }) {
  const confirm = plan.confirm(model);
  const refused = plan.refused || null;
  const before = await tools.before();
  const confirmedBefore = matching(before, confirm).length;
  const refusedBefore = refused ? matching(before, refused).length : 0;
  await tools.type(plan.command(model));
  const answered = new Set();
  const deadline = now() + CONFIRM_TIMEOUT_MS;
  while (now() < deadline) {
    await wait(POLL_MS);
    const lines = await tools.lines();
    const confirmations = matching(lines, confirm);
    if (confirmations.length > confirmedBefore) {
      return { confirmation: confirmations[confirmations.length - 1].trim() };
    }
    if (refused) {
      const refusals = matching(lines, refused);
      if (refusals.length > refusedBefore) throw new ModelSwitchError('model_refused', refusals[refusals.length - 1].trim());
    }
    const screen = await tools.screen();
    for (const dialog of plan.dialogs || []) {
      if (answered.has(dialog) || !screen.some(row => dialog.screen.test(row))) continue;
      answered.add(dialog);
      await tools.keys(dialog.keys);
    }
  }
  throw new ModelSwitchError('model_switch_not_confirmed');
}

async function switchModel(plan, model, tools, timing) {
  validateModel(model);
  if (typeof plan.run === 'function') return plan.run(model, tools, { ...timing, ModelSwitchError, CONFIRM_TIMEOUT_MS, POLL_MS });
  return runCommandPlan(plan, model, tools, timing);
}

module.exports = {
  CONFIRM_TIMEOUT_MS, POLL_MS, RENDER_BYTES, ModelSwitchError, escapeRegExp, renderLines, runCommandPlan, switchModel, validateModel
};
