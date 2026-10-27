'use strict';

const MAX_FOLLOW_UP_CHARS = 16_000;
// A handover beginning with '/' must be a message, not another CLI command.
const HANDOVER_HEADER = '[AgentHub context handover]\n';
// Typing the handover into the fresh conversation, each attempt after the input looked ready
// again. A first input can be dropped by a CLI still redrawing after its clear (#336), and the
// next attempt finds it ready; a third failure is not a timing accident any more.
const DELIVERY_ATTEMPTS = 3;
// Rounds at a later safe point once the attempts above are spent: the person may have closed a
// menu or finished a turn by then. After that the handover stays saved for the next agent start.
const FALLBACK_ROUNDS = 3;
const RESULT_NOTE = 'The handover was saved before the clear and is sent as the first message of the fresh ' +
  'conversation once this turn ends. Finish this turn now.';

function validateFollowUp(value) {
  if (typeof value !== 'string' || !value.trim() || value.length > MAX_FOLLOW_UP_CHARS ||
      /[\x00-\x08\x0b\x0c\x0e-\x1f\x7f-\x9f]/.test(value)) throw new Error('invalid_follow_up');
  return value.replace(/\r\n?/g, '\n');
}

// The agent the clear was meant for is gone or going: a restart, an exit, a runtime update.
// The saved handover is resumed by the next agent start instead of being retried here.
const AGENT_GONE = new Set(['context_clear_unavailable', 'context_clear_agent_restarted']);

// The scheduler owns turn/menu readiness and reserves the input channel for this action.
// Keeping that decision outside this sequencer lets fleet messages and context changes use
// the same safe point; an MCP tool call is still part of the turn being cleared.
//
// The handover is written to `store` before anything is typed and removed only once the CLI's
// own transcript holds it (#336). It used to live in this closure alone: a readiness check that
// threw after a successful clear, an input the CLI dropped, or a restart in between left a fresh
// conversation without its handover, and nothing but one terminal line said so.
function createContextClear({
  scheduleAtSafePoint, clear, waitForClear, waitForInput, deliver, verify,
  store = { load: () => null, save: () => {}, remove: () => {} },
  onError = () => {}, onDelivered = () => {}, log = () => {}
}) {
  let pending = false;
  let executing = false;

  async function sequence(handover, { clearFirst, round }) {
    const text = HANDOVER_HEADER + handover;
    let previous = null;
    if (clearFirst) {
      previous = await clear();
      store.save({ followUp: handover, cleared: true });
      try { await waitForClear(previous); } catch (error) {
        if (AGENT_GONE.has(error.message)) throw error;
        // Whether the clear went through is not known. Holding the handover back loses it when
        // it did; sending it into a conversation that was not cleared only repeats known state.
        log('context clear not confirmed (' + error.message + '); the handover is sent anyway');
      }
    }
    let lastError = new Error('context_clear_handover_not_delivered');
    for (let attempt = 1; attempt <= DELIVERY_ATTEMPTS; attempt++) {
      try { await waitForInput(); } catch (error) {
        if (AGENT_GONE.has(error.message)) throw error;
        lastError = error;
        log('context handover attempt ' + attempt + ': ' + error.message);
        continue;
      }
      const submitted = await deliver(text, { attempt });
      const seen = await verify(text, previous, { submitted: submitted !== false });
      if (seen) {
        store.remove();
        onDelivered({ fresh: seen.fresh !== false, attempt, round });
        return;
      }
      lastError = new Error('context_clear_handover_not_in_transcript');
      log('context handover attempt ' + attempt + ': not in the transcript');
    }
    throw lastError;
  }

  function enqueue(handover, options) {
    pending = true;
    scheduleAtSafePoint(async () => {
      executing = true;
      let next = null;
      try {
        await sequence(handover, options);
        pending = false;
        return true;
      } catch (error) {
        const deferred = AGENT_GONE.has(error.message);
        const retry = !deferred && options.round < FALLBACK_ROUNDS;
        onError(error, { retry, deferred });
        // Typed at the next safe point, the way a priority message is, without clearing again:
        // the fresh conversation is there already, only its first message is missing.
        if (retry) next = { clearFirst: false, round: options.round + 1 };
        else pending = false;
        return false;
      } finally {
        executing = false;
        if (next) enqueue(handover, next);
      }
    }, { atStart: options.atStart === true });
  }

  return {
    get pending() { return pending; },
    get executing() { return executing; },
    schedule(followUp) {
      const handover = validateFollowUp(followUp);
      if (pending) throw new Error('context_clear_pending');
      // Saved before it is scheduled, so a restart between here and the handover finds it.
      store.save({ followUp: handover, cleared: false });
      try {
        enqueue(handover, { clearFirst: true, round: 0 });
      } catch (error) {
        pending = false;
        store.remove();
        throw error;
      }
      return { scheduled: true, handover: RESULT_NOTE };
    },
    /**
     * At an agent start: a handover an earlier run saved and did not get to deliver. The clear is
     * done again, because the restarted CLI resumes the conversation it last saved, which is the
     * old one when the fresh one was never written to (Codex creates its rollout with the first
     * message only).
     */
    resume() {
      if (pending) return false;
      let saved = null;
      try { saved = store.load(); } catch {}
      let handover = null;
      try { handover = saved && validateFollowUp(saved.followUp); } catch {}
      if (!handover) {
        if (saved) store.remove();
        return false;
      }
      log('context handover saved by an earlier run; clearing and sending it once the agent is ready');
      enqueue(handover, { clearFirst: true, round: 0, atStart: true });
      return true;
    }
  };
}

module.exports = { createContextClear, validateFollowUp, MAX_FOLLOW_UP_CHARS, HANDOVER_HEADER, DELIVERY_ATTEMPTS, FALLBACK_ROUNDS };
