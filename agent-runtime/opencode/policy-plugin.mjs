import { createGate } from './policy-gate.mjs';
import { createTurnNotifier } from './turn-notify.mjs';

// OpenCode treats every function this module exports as a plugin and calls it at startup, so
// this file exports the plugin and nothing else; the logic lives in policy-gate.mjs and
// turn-notify.mjs.
export const AgentHubPolicy = async (input) => {
  const gate = createGate();
  const notify = createTurnNotifier({ client: input && input.client });
  return {
    'tool.execute.before': async (input, output) => {
      await gate(input && input.tool, output && output.args);
    },
    // A failed relay must never disturb the session, so nothing here throws.
    event: async ({ event }) => {
      await notify(event).catch(() => {});
    }
  };
};
