'use strict';

// common/fleet-turn-hook.mjs, the Codex end of the next-turn delivery (#173). Payloads use the
// field names Codex 0.160.0 hands its managed hooks (codex-rs/hooks/src/schema.rs at rust-v0.160.0).
const assert = require('node:assert/strict');
const { execFile } = require('node:child_process');
const { createServer } = require('node:http');
const fs = require('node:fs');
const path = require('node:path');
const test = require('node:test');

const runtimeRoot = path.join(__dirname, '..', '..');
const codexWrapper = path.join(runtimeRoot, 'codex', 'fleet-turn-hook.js');

const thread = {
  session_id: '01a122d9-55f0-73d3-8adf-6d0ea090b750',
  turn_id: '01a122d9-5625-7020-8e72-ad62a3340e92',
  transcript_path: '/home/agent/.codex/sessions/2026/10/18/rollout-2026-10-18T16-47-11-01a122d9.jsonl',
  cwd: '/workspace', model: 'gpt-5.4', permission_mode: 'default'
};
const stop = { ...thread, hook_event_name: 'Stop', stop_hook_active: false, last_assistant_message: 'Done.' };
const env = { AGENTHUB_MODE: 'interactive', AGENTHUB_CALLBACK_TOKEN: 'tok', AGENTHUB_PORT: '8123' };

function recorder(answer = {}) {
  const calls = [];
  const fetchImpl = async (url, init) => {
    calls.push({ url, token: init.headers['X-Agent-Token'], body: JSON.parse(init.body) });
    return { ok: true, json: async () => answer };
  };
  return { calls, fetchImpl };
}

test('each Codex event is reported to the session agent under its own name', async () => {
  const { run } = await import('../../common/fleet-turn-hook.mjs');
  const { calls, fetchImpl } = recorder();

  await run({ ...thread, hook_event_name: 'UserPromptSubmit', prompt: 'hi' }, env, fetchImpl);
  await run({ ...thread, hook_event_name: 'PermissionRequest', tool_name: 'shell' }, env, fetchImpl);
  await run(stop, env, fetchImpl);
  await run({ ...thread, hook_event_name: 'Interrupt' }, env, fetchImpl);
  await run({ ...thread, hook_event_name: 'PostToolUse' }, env, fetchImpl);

  assert.deepEqual(calls.map(call => call.body.event), ['prompt', 'permission', 'stop', 'idle']);
  assert.equal(calls[0].url, 'http://127.0.0.1:8123/agenthub/turn');
  assert.equal(calls[0].token, 'tok');
});

test('a Stop with messages blocks with them as the reason; other events and empty answers print nothing', async () => {
  const { run } = await import('../../common/fleet-turn-hook.mjs');
  const text = '[AgentHub message from agent "Code Reviewer" (rev-1)]\nPlease fix MR 42';

  assert.deepEqual(await run(stop, env, recorder({ text }).fetchImpl), { decision: 'block', reason: text });
  // Including the continuation a Stop hook caused: messages that arrived during it still go.
  assert.deepEqual(await run({ ...stop, stop_hook_active: true }, env, recorder({ text }).fetchImpl),
    { decision: 'block', reason: text });
  assert.equal(await run(stop, env, recorder({}).fetchImpl), null);
  assert.equal(await run({ ...thread, hook_event_name: 'UserPromptSubmit' }, env, recorder({ text }).fetchImpl), null);
});

test('helper threads, subagents and autonomous runs report nothing', async () => {
  const { run } = await import('../../common/fleet-turn-hook.mjs');
  const { calls, fetchImpl } = recorder({ text: 'x' });

  // The TUI's title generation: its own session without a rollout file.
  await run({ ...stop, transcript_path: null }, env, fetchImpl);
  await run({ ...thread, hook_event_name: 'UserPromptSubmit', transcript_path: null }, env, fetchImpl);
  await run({ ...thread, hook_event_name: 'UserPromptSubmit', agent_id: 'sub-1' }, env, fetchImpl);
  await run(stop, { ...env, AGENTHUB_MODE: 'autonomous' }, fetchImpl);

  assert.deepEqual(calls, []);
});

test('a session agent that cannot be reached leaves the turn alone', async () => {
  const { run } = await import('../../common/fleet-turn-hook.mjs');
  assert.equal(await run(stop, env, async () => { throw new Error('ECONNREFUSED'); }), null);
  assert.equal(await run(stop, { ...env, AGENTHUB_CALLBACK_TOKEN: '' }, recorder({ text: 'x' }).fetchImpl), null);
});

test('the Codex wrapper prints the block decision as its only output', async () => {
  const text = '[AgentHub message from outside the fleet]\nhello';
  const server = createServer((request, response) => {
    let body = '';
    request.on('data', chunk => { body += chunk; });
    request.on('end', () => {
      const ok = request.url === '/agenthub/turn' && request.headers['x-agent-token'] === 'tok' && JSON.parse(body).event === 'stop';
      response.writeHead(ok ? 200 : 400, { 'Content-Type': 'application/json' });
      response.end(JSON.stringify(ok ? { text } : {}));
    });
  });
  await new Promise(resolve => server.listen(0, '127.0.0.1', resolve));
  try {
    const stdout = await new Promise((resolve, reject) => {
      const child = execFile('node', [codexWrapper], {
        env: { ...process.env, ...env, AGENTHUB_PORT: String(server.address().port) }
      }, (error, out) => error ? reject(error) : resolve(out));
      child.stdin.end(JSON.stringify(stop));
    });
    assert.deepEqual(JSON.parse(stdout), { decision: 'block', reason: text });
  } finally {
    server.close();
  }
});

test('requirements.toml runs the hook on turn events, after the policy hook on approvals', () => {
  const requirements = fs.readFileSync(path.join(runtimeRoot, 'codex', 'requirements.toml'), 'utf8');
  const command = 'command = "node /opt/session-agent/codex/fleet-turn-hook.js"';
  // Everything from an event's [[hooks.<Event>]] header to the next event's header.
  const section = name => {
    const headers = [...requirements.matchAll(/^\[\[hooks\.(\w+)\]\]$/gm)];
    const index = headers.findIndex(match => match[1] === name);
    return requirements.slice(headers[index].index, headers[index + 1]?.index);
  };
  for (const event of ['UserPromptSubmit', 'PermissionRequest', 'Stop', 'Interrupt']) {
    assert.ok(section(event).includes(command), event + ' runs fleet-turn-hook.js');
  }
  const permission = section('PermissionRequest');
  assert.ok(permission.indexOf('policy-hook.js') < permission.indexOf('fleet-turn-hook.js'));
  assert.equal(require('../../codex/driver.js').turnHooks, true);
});
