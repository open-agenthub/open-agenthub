'use strict';

const MAX_FOLLOW_UP_CHARS = 16_000;

function validateFollowUp(value) {
  if (typeof value !== 'string' || !value.trim() || value.length > MAX_FOLLOW_UP_CHARS ||
      /[\x00-\x08\x0b\x0c\x0e-\x1f\x7f]/.test(value)) throw new Error('invalid_follow_up');
  return value.replace(/\r\n?/g, '\n');
}

// The scheduler owns turn/menu readiness and reserves the input channel for this action.
// Keeping that decision outside this sequencer lets fleet messages and context changes use
// the same safe point; an MCP tool call is still part of the turn being cleared.
function createContextClear({ scheduleAtSafePoint, clear, waitForClear, deliver, onError = () => {} }) {
  let pending = false;
  let executing = false;
  return {
    get pending() { return pending; },
    get executing() { return executing; },
    schedule(followUp) {
      const handover = validateFollowUp(followUp);
      if (pending) throw new Error('context_clear_pending');
      pending = true;
      try {
        scheduleAtSafePoint(async () => {
          executing = true;
          try {
            const previous = await clear();
            await waitForClear(previous);
            // A handover beginning with '/' must be a message, not another CLI command.
            await deliver('[AgentHub context handover]\n' + handover);
          } catch (error) {
            onError(error);
          } finally {
            executing = false;
            pending = false;
          }
        });
      } catch (error) {
        pending = false;
        throw error;
      }
      return { scheduled: true };
    }
  };
}

module.exports = { createContextClear, validateFollowUp, MAX_FOLLOW_UP_CHARS };
