'use strict';

// A turn that stops making progress without ending (#562, docs/stalled-turns.md). Codex 0.167
// waits with no limit on a model response the server keeps alive with events but never
// finishes, and on a model endpoint it cannot reach ("Reconnecting... waiting for network").
// Messages queued during that time wait for a tool call or a turn end that never comes. Its
// own timeouts cannot catch this: a keep-alive event resets stream_idle_timeout_ms.
//
// Progress is what the agent did, never what the TUI animates: a hook event, a transcript that
// grew, or a screen that changed after the driver took out its spinner and elapsed-time
// counter. Codex redraws "Working (17h 16m 37s …)" every second, so raw PTY output alone would
// count every stalled turn as busy.

const DEFAULT_NOTICE_MINUTES = 20;
const DEFAULT_INTERRUPT_MINUTES = 60;
const MAX_MINUTES = 24 * 60;
const CHECK_MS = 15_000;

/** A minutes setting from the environment: unset or empty = the default, 0 = off. */
function minutesSetting(raw, fallback) {
  if (raw === undefined || raw === '') return fallback;
  const minutes = Number(raw);
  if (!Number.isFinite(minutes) || minutes <= 0) return 0;
  return Math.min(minutes, MAX_MINUTES);
}

function stallSettings(env) {
  return {
    noticeMs: minutesSetting(env.AGENTHUB_STALLED_TURN_NOTICE_MINUTES, DEFAULT_NOTICE_MINUTES) * 60_000,
    interruptMs: minutesSetting(env.AGENTHUB_STALLED_TURN_INTERRUPT_MINUTES, DEFAULT_INTERRUPT_MINUTES) * 60_000
  };
}

function createStallWatch({ now, noticeMs, interruptMs }) {
  let busySince = null;
  let lastProgress = 0;
  let key = null;
  let inputSinceSample = false;
  let reconnecting = false;
  let interrupted = false;

  const stalledMs = () => busySince === null ? 0 : now() - lastProgress;
  return {
    /** Whether a turn runs; a new one starts the clock, an ended one stops it. */
    busy(value) {
      if (value && busySince === null) {
        busySince = lastProgress = now();
        key = null;
        reconnecting = false;
        interrupted = false;
      } else if (!value) busySince = null;
    },
    progress() { if (busySince !== null) lastProgress = now(); },
    /** Keys written to the agent. A message typed into a stalled CLI redraws its queue panel;
     * that is the person's doing, not the agent's, and must not hide the stall. */
    input() { inputSinceSample = true; },
    /** What the driver made of the screen, plus anything else that only changes with progress. */
    observe(sample) {
      if (busySince === null || !sample) return;
      if (key !== null && sample.key !== key && !inputSinceSample) lastProgress = now();
      key = sample.key;
      inputSinceSample = false;
      reconnecting = sample.reconnecting === true;
    },
    /** For the Workspace: null, or how long the turn has gone without progress. */
    stalled() {
      const ms = stalledMs();
      if (!noticeMs || ms < noticeMs) return null;
      return { minutes: Math.floor(ms / 60_000), reason: reconnecting ? 'reconnecting' : 'no_progress' };
    },
    /** Once per stalled turn: the minutes without progress when the interrupt limit is reached. */
    takeInterrupt() {
      const ms = stalledMs();
      if (!interruptMs || interrupted || ms < interruptMs) return null;
      interrupted = true;
      return Math.floor(ms / 60_000);
    }
  };
}

module.exports = { createStallWatch, stallSettings, minutesSetting, CHECK_MS, DEFAULT_NOTICE_MINUTES, DEFAULT_INTERRUPT_MINUTES };
