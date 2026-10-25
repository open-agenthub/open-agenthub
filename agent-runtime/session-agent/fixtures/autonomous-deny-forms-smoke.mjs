// #244: a command the owner's policy does not allow stays denied in every wrapping when
// auto-approve is off, through both unattended hook paths: Codex's real policy-hook.js (fed the
// PreToolUse payload the CLI sends) and OpenCode's real policy gate. The callback is
// tests/AutonomousPolicyProof started with autoApprove=false, i.e. the backend's real matcher.
//   POLICY_URL=http://127.0.0.1:<port> node autonomous-deny-forms-smoke.mjs
import { spawnSync } from 'node:child_process';
import { fileURLToPath } from 'node:url';
import { createGate } from '../../opencode/policy-gate.mjs';

const hook = fileURLToPath(new URL('../../codex/policy-hook.js', import.meta.url));
const env = { AGENTHUB_MODE: 'autonomous', AGENTHUB_CALLBACK_URL: `${process.env.POLICY_URL}/internal/sessions/proof`,
  AGENTHUB_CALLBACK_TOKEN: 'synthetic-callback' };
const FORMS = ['git push >/dev/null', '$(git push)', '`git push`', 'x=1; git push', "sh -c 'git push'",
  'eval "git push"', 'env A=1 git push', 'g""it push'];
const gate = createGate({ env, servers: [] });
let allowed = 0;
for (const command of FORMS) {
  const result = spawnSync(process.execPath, [hook], { env: { ...process.env, ...env }, encoding: 'utf8',
    input: JSON.stringify({ hook_event_name: 'PreToolUse', tool_name: 'Bash', tool_input: { command } }) });
  const codex = /"permissionDecision":"deny"/.test(result.stdout) ? 'deny' : 'ALLOW';
  let opencode = 'ALLOW';
  try { await gate('bash', { command }); } catch { opencode = 'deny'; }
  if (codex !== 'deny' || opencode !== 'deny') allowed++;
  console.log(`codex=${codex} opencode=${opencode} :: ${command}`);
}
process.exitCode = allowed ? 1 : 0;
