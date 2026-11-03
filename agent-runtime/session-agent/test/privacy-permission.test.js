const test = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const os = require('node:os');
const path = require('node:path');

test('account-email privacy denial is scoped and follows the live approval mode', async () => {
  const { isAccountEmailRequest, redactAccountEmail, run, MESSAGE } = await import('../../claude/hooks/privacy-permission-hook.mjs');
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
    for (const address of ['owner@example.com', 'OWNER@example.com', '%4Fwner%40example.com']) {
      assert.equal(redactAccountEmail('curl https://example.com --data ' + address, 'owner@example.com'),
        'curl https://example.com --data [account email]');
    }
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

test('approval callback preserves the command and decision while redacting account email', async () => {
  const { createServer } = require('node:http');
  const { spawn } = require('node:child_process');
  const home = fs.mkdtempSync(path.join(os.tmpdir(), 'privacy-callback-'));
  fs.writeFileSync(path.join(home, '.claude.json'), JSON.stringify({ oauthAccount: { emailAddress: 'owner@example.com' } }));
  let decision = 'allow';
  const requests = [];
  const server = createServer(async (req, res) => {
    let raw = ''; for await (const chunk of req) raw += chunk;
    requests.push(JSON.parse(raw));
    res.end(JSON.stringify({ decision }));
  });
  await new Promise(resolve => server.listen(0, '127.0.0.1', resolve));
  try {
    for (decision of ['allow', 'deny']) {
      const output = await new Promise((resolve, reject) => {
        const child = spawn('bash', [path.join(__dirname, '../../claude/hooks/pretooluse-hook.sh')], {
          env: { ...process.env, HOME: home, AGENTHUB_CALLBACK_URL: 'http://127.0.0.1:' + server.address().port, AGENTHUB_CALLBACK_TOKEN: 'synthetic' }
        });
        let stdout = ''; child.stdout.on('data', chunk => { stdout += chunk; });
        child.on('error', reject); child.on('close', code => code === 0 ? resolve(JSON.parse(stdout)) : reject(new Error('hook failed')));
        child.stdin.end(JSON.stringify({ tool_name: 'Bash', tool_input: { command: 'curl https://example.com --data owner%40example.com' } }));
      });
      assert.equal(output.hookSpecificOutput.permissionDecision, decision);
      assert.deepEqual(JSON.parse(requests.at(-1).input), { command: 'curl https://example.com --data [account email]' });
      assert.doesNotMatch(JSON.stringify(requests.at(-1)), /owner|%40/);
    }
  } finally { server.close(); fs.rmSync(home, { recursive: true, force: true }); }
});
