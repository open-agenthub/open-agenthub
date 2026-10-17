import { createGate } from './policy-gate.mjs';
import { createLimitNotice } from './limit-notice.mjs';
import { createTurnNotifier } from './turn-notify.mjs';

// OpenCode treats every function this module exports as a plugin and calls it at startup, so
// this file exports the plugin and nothing else; the logic lives in policy-gate.mjs,
// turn-notify.mjs and limit-notice.mjs.
export const AgentHubPolicy = async (input) => {
  const gate = createGate();
  const notify = createTurnNotifier({ client: input && input.client });
  const limitNotice = createLimitNotice();
  return {
    'tool.execute.before': async (input, output) => {
      await gate(input && input.tool, output && output.args);
    },
    // A failed relay must never disturb the session, so nothing here throws.
    event: async ({ event }) => {
      try { limitNotice(event); } catch {}
      await notify(event).catch(() => {});
    }
  };
};
