// #244 for OpenCode: its real policy gate, as the plugin calls it for a bash tool call, against
// tests/AutonomousPolicyProof (the backend's real AgentPolicyMatcher, Autonomous + auto-approve).
//   POLICY_URL=http://127.0.0.1:<port> node opencode-autonomous-policy-smoke.mjs
import { createGate } from '../../opencode/policy-gate.mjs';

const gate = createGate({
  env: { AGENTHUB_MODE: 'autonomous', AGENTHUB_CALLBACK_URL: `${process.env.POLICY_URL}/internal/sessions/proof`,
    AGENTHUB_CALLBACK_TOKEN: 'synthetic-callback' },
  servers: []
});
let blocked = 0;
for (const command of ['echo smoke > smoke.txt && cat smoke.txt && uname -m', 'uname -m', 'echo "$HOME" | wc -c']) {
  try {
    await gate('bash', { command, description: 'smoke' });
    console.log(`ran     :: ${command}`);
  } catch (error) {
    blocked++;
    console.log(`BLOCKED :: ${command} :: ${error.message}`);
  }
}
process.exitCode = blocked ? 1 : 0;
