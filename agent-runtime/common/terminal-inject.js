'use strict';

// The pauses the hub's AgentTerminal.SendInputAsync uses, and for the same reason: Claude Code's
// TUI treats a fast multi-character burst as a paste, and a "\r" inside that burst becomes a
// newline in the input box instead of submitting it. Enter goes out as its own write after a
// pause so it is read as a keypress. Codex's TUI accepts the same sequence (docs/chat-relay.md).
const SUBMIT_PAUSE_MS = 300;
// After an Escape the TUI needs a moment to cancel the running turn before it accepts input;
// text written in the same instant lands in the prompt box of the turn that is still ending.
const INTERRUPT_PAUSE_MS = 300;
const ESCAPE = String.fromCharCode(27);

/**
 * Types `text` into a PTY the way a person would, optionally pressing Escape first to stop the
 * agent's current turn. Resolves once Enter has been written. `wait` is injectable so tests run
 * without real pauses.
 */
function injectTerminalInput(term, text, options = {}) {
  const wait = options.wait || (ms => new Promise(resolve => setTimeout(resolve, ms)));
  const steps = [];
  if (options.interrupt) {
    steps.push(() => term.write(ESCAPE));
    steps.push(() => wait(INTERRUPT_PAUSE_MS));
  }
  steps.push(() => term.write(text));
  steps.push(() => wait(SUBMIT_PAUSE_MS));
  steps.push(() => term.write('\r'));
  return steps.reduce((chain, step) => chain.then(step), Promise.resolve());
}

/**
 * The text a fleet message is typed as: a header naming the sender, so the agent knows this is
 * a peer's message and not its owner typing, then the body. Header and body go out in one burst
 * on purpose — the TUI reads the burst as a paste and keeps the newlines inside the input box,
 * and only the separate, delayed Enter submits it.
 */
function formatFleetMessage(message) {
  const header = message.from
    ? '[AgentHub message from agent "' + (message.fromTitle || message.from) + '" (' + message.from + ')]'
    : '[AgentHub message from outside the fleet]';
  return header + '\n' + message.body;
}

module.exports = { injectTerminalInput, formatFleetMessage, SUBMIT_PAUSE_MS, INTERRUPT_PAUSE_MS, ESCAPE };
