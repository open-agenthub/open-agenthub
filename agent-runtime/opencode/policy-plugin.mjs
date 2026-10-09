import { createGate } from './policy-gate.mjs';

// OpenCode treats every function this module exports as a plugin and calls it at startup, so
// this file exports the plugin and nothing else; the logic lives in policy-gate.mjs.
export const AgentHubPolicy = async () => {
  const gate = createGate();
  return {
    'tool.execute.before': async (input, output) => {
      await gate(input && input.tool, output && output.args);
    }
  };
};
