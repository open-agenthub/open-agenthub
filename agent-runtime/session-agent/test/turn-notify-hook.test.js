'use strict';

const assert = require('node:assert/strict');
const { execFile } = require('node:child_process');
const { createServer } = require('node:http');
const fs = require('node:fs');
const os = require('node:os');
const path = require('node:path');
const test = require('node:test');

const runtimeRoot = path.join(__dirname, '..', '..');
const hookPath = path.join(runtimeRoot, 'common', 'turn-notify-hook.mjs');
const codexWrapper = path.join(runtimeRoot, 'codex', 'turn-notify-hook.js');

// Field names as Codex 0.160.0 hands them to a managed Stop hook (probed against the
// pinned CLI in the runtime image; see docs/chat-relay.md).
const codexStop = {
  session_id: '01a122d9-55f0-73d3-8adf-6d0ea090b750',
  turn_id: '01a122d9-5625-7020-8e72-ad62a3340e92',
  transcript_path: '/home/agent/.codex/sessions/2026/10/09/rollout-2026-10-09T22-47-11-01a122d9.jsonl',
  cwd: '/workspace',
  hook_event_name: 'Stop',
  model: 'gpt-5.4',
  permission_mode: 'default',
  stop_hook_active: false,
  last_assistant_message: 'Which branch should I use?'
};
// Cursor's stop payload (cursor.com/docs/hooks): no transcript, no message.
const cursorStop = { conversation_id: 'c1', generation_id: 'g1', hook_event_name: 'stop',
  status: 'completed', loop_count: 0, transcript_path: null };

const interactive = { AGENTHUB_MODE: 'interactive' };

function tempDir() {
  const dir = fs.mkdtempSync(path.join(os.tmpdir(), 'turn-notify-'));
  test.after(() => fs.rmSync(dir, { recursive: true, force: true }));
  return dir;
}

function rolloutLine(role, text, type = 'output_text') {
  return JSON.stringify({ timestamp: 't', type: 'response_item',
    payload: { type: 'message', id: 'm', role, content: [{ type, text }] } });
}

function startCallbackServer(status = 204) {
  const requests = [];
  const server = createServer((request, response) => {
    let body = '';
    request.on('data', chunk => { body += chunk; });
    request.on('end', () => {
      requests.push({ path: request.url, token: request.headers['x-agent-token'], body: JSON.parse(body) });
      response.writeHead(status);
      response.end();
    });
  });
  return new Promise(resolve => server.listen(0, '127.0.0.1', () => resolve({
    requests,
    url: `http://127.0.0.1:${server.address().port}/internal/sessions/s1`,
    close: () => new Promise(done => server.close(done))
  })));
}

// Asynchronous on purpose: the callback server lives in this process, and a synchronous
// exec would block the loop that has to accept the hook's connection.
function runHook(file, input, env) {
  return new Promise((resolve, reject) => {
    const child = execFile(process.execPath, [file], {
      env: { ...process.env, AGENTHUB_MODE: undefined, ...env },
      encoding: 'utf8', timeout: 10_000
    }, (error, stdout) => error ? reject(error) : resolve(stdout));
    child.stdin.end(typeof input === 'string' ? input : JSON.stringify(input));
  });
}

test('an interactive Codex turn ends in a question carrying the last assistant message', async () => {
  const { decide } = await import('../../common/turn-notify-hook.mjs');
  assert.deepEqual(decide(codexStop, interactive),
    { event: 'question', message: 'Which branch should I use?' });
});

test('exec, autonomous and scheduled sessions send nothing: "finished" follows the process', async () => {
  // A "question" there would open a thread for an answer nobody can give.
  const { decide } = await import('../../common/turn-notify-hook.mjs');
  for (const mode of ['autonomous', 'scheduled', 'Autonomous']) {
    assert.equal(decide(codexStop, { AGENTHUB_MODE: mode }), null, mode);
    assert.equal(decide(cursorStop, { AGENTHUB_MODE: mode }), null, mode);
  }
  // Unset means interactive, as it does for the driver and the policy hook.
  assert.ok(decide(codexStop, {}));
});

test('the continuation a Stop hook caused is not a second question', async () => {
  const { decide } = await import('../../common/turn-notify-hook.mjs');
  assert.equal(decide({ ...codexStop, stop_hook_active: true }, interactive), null);
  assert.equal(decide({ ...cursorStop, loop_count: 1 }, interactive), null);
});

test('the TUI\'s title-generation helper thread is not the user\'s turn', async () => {
  // Probed on 0.160.0: after the first answer the TUI runs a sub-session that writes no
  // rollout file, and its Stop arrives with the generated title as the last message.
  const { decide } = await import('../../common/turn-notify-hook.mjs');
  const helper = { ...codexStop, session_id: 'other', transcript_path: null,
    permission_mode: 'bypassPermissions', last_assistant_message: 'Answer the branch question' };
  assert.equal(decide(helper, interactive), null);
  assert.equal(decide({ ...helper, transcript_path: undefined }, interactive), null);
  // A missing rollout *file* is a different matter: the path is given, so it is a user turn.
  assert.ok(decide({ ...codexStop, last_assistant_message: null, transcript_path: '/nonexistent/rollout.jsonl' }, interactive));
});

test('Cursor gets a generic question; an abort is someone at the keyboard, an error is not', async () => {
  const { decide } = await import('../../common/turn-notify-hook.mjs');
  const body = decide(cursorStop, interactive);
  assert.equal(body.event, 'question');
  assert.match(body.message, /waiting for your reply/);
  assert.equal(decide({ ...cursorStop, status: 'aborted' }, interactive), null);
  assert.ok(decide({ ...cursorStop, status: 'error' }, interactive));
});

test('without last_assistant_message the transcript\'s last assistant text is used', async () => {
  const { decide, lastAssistantMessage } = await import('../../common/turn-notify-hook.mjs');
  const dir = tempDir();
  const transcript = path.join(dir, 'rollout.jsonl');
  fs.writeFileSync(transcript, [
    JSON.stringify({ type: 'session_meta', payload: { id: 'x' } }),
    rolloutLine('user', 'Say something.', 'input_text'),
    rolloutLine('assistant', 'First answer.'),
    rolloutLine('user', 'And again?', 'input_text'),
    rolloutLine('assistant', 'Second answer, the one to relay.'),
    JSON.stringify({ type: 'event_msg', payload: { type: 'task_complete', last_agent_message: 'Second answer, the one to relay.' } }),
    'not json at all',
    ''
  ].join('\n'));

  assert.equal(lastAssistantMessage(transcript), 'Second answer, the one to relay.');
  const body = decide({ ...codexStop, last_assistant_message: null, transcript_path: transcript }, interactive);
  assert.equal(body.message, 'Second answer, the one to relay.');

  // Claude-style transcripts are read as well, so the extraction is not tied to one rollout shape.
  const claude = path.join(dir, 'claude.jsonl');
  fs.writeFileSync(claude, [
    JSON.stringify({ type: 'user', message: { role: 'user', content: [{ type: 'text', text: 'hi' }] } }),
    JSON.stringify({ type: 'assistant', message: { role: 'assistant', content: [{ type: 'text', text: 'Hello from Claude' }] } })
  ].join('\n'));
  assert.equal(lastAssistantMessage(claude), 'Hello from Claude');
});

test('a missing, empty or unreadable transcript falls back to the generic message', async () => {
  const { decide, lastAssistantMessage } = await import('../../common/turn-notify-hook.mjs');
  const dir = tempDir();
  const empty = path.join(dir, 'empty.jsonl');
  fs.writeFileSync(empty, '');
  assert.equal(lastAssistantMessage(empty), null);
  assert.equal(lastAssistantMessage(path.join(dir, 'missing.jsonl')), null);
  assert.equal(lastAssistantMessage(null), null);
  // A transcript with only user turns has nothing to relay either.
  const users = path.join(dir, 'users.jsonl');
  fs.writeFileSync(users, rolloutLine('user', 'hello', 'input_text'));
  assert.equal(lastAssistantMessage(users), null);

  const body = decide({ ...codexStop, last_assistant_message: '   ', transcript_path: path.join(dir, 'missing.jsonl') }, interactive);
  assert.match(body.message, /waiting for your reply/);
});

test('the message is clipped like the Claude hook clips it, never on a split surrogate pair', async () => {
  const { decide } = await import('../../common/turn-notify-hook.mjs');
  const long = 'x'.repeat(11_999) + '😀' + 'y'.repeat(100);
  const body = decide({ ...codexStop, last_assistant_message: long }, interactive);
  assert.equal(body.message.length, 11_999);
  assert.ok(!/[\uD800-\uDBFF]$/.test(body.message));
});

test('run as a command in interactive mode it POSTs the question to /notify', async () => {
  const server = await startCallbackServer();
  try {
    const out = await runHook(hookPath, codexStop, {
      ...interactive, AGENTHUB_CALLBACK_URL: server.url, AGENTHUB_CALLBACK_TOKEN: 'tok'
    });
    assert.equal(out, '', 'Codex rejects plain text on Stop, so stdout stays empty');
    assert.equal(server.requests.length, 1);
    assert.equal(server.requests[0].path, '/internal/sessions/s1/notify');
    assert.equal(server.requests[0].token, 'tok');
    assert.deepEqual(server.requests[0].body, { event: 'question', message: 'Which branch should I use?' });
  } finally {
    await server.close();
  }
});

test('run as a command in exec mode it sends nothing', async () => {
  const server = await startCallbackServer();
  try {
    const out = await runHook(hookPath, codexStop, {
      AGENTHUB_MODE: 'autonomous', AGENTHUB_CALLBACK_URL: server.url, AGENTHUB_CALLBACK_TOKEN: 'tok'
    });
    assert.equal(out, '');
    assert.equal(server.requests.length, 0);
  } finally {
    await server.close();
  }
});

test('nothing that can go wrong breaks the turn: bad payload, no callback, dead callback, 500', async () => {
  const env = { ...interactive, AGENTHUB_CALLBACK_TOKEN: 'tok' };
  for (const input of ['', 'not json', '[]', 'null']) {
    assert.equal(await runHook(hookPath, input, { ...env, AGENTHUB_CALLBACK_URL: 'http://127.0.0.1:9/x' }), '');
  }
  assert.equal(await runHook(hookPath, codexStop, { ...env, AGENTHUB_CALLBACK_URL: undefined }), '');
  assert.equal(await runHook(hookPath, codexStop, { ...env, AGENTHUB_CALLBACK_URL: 'ftp://127.0.0.1/x' }), '');
  const failing = await startCallbackServer(500);
  try {
    assert.equal(await runHook(hookPath, codexStop, { ...env, AGENTHUB_CALLBACK_URL: failing.url }), '');
    assert.equal(failing.requests.length, 1);
  } finally {
    await failing.close();
  }
});

test('the codex wrapper runs the shared hook from its managed directory', async () => {
  const server = await startCallbackServer();
  try {
    await runHook(codexWrapper, codexStop, {
      ...interactive, AGENTHUB_CALLBACK_URL: server.url, AGENTHUB_CALLBACK_TOKEN: 'tok'
    });
    assert.equal(server.requests.length, 1);
    assert.equal(server.requests[0].body.event, 'question');
  } finally {
    await server.close();
  }
});

test('Codex and Cursor wire the relay notification to their end-of-turn hook', () => {
  const requirements = fs.readFileSync(path.join(runtimeRoot, 'codex', 'requirements.toml'), 'utf8');
  // Under managed_dir and via the wrapper, for the same reason as the skill reminder.
  assert.match(requirements, /command = "node \/opt\/session-agent\/codex\/turn-notify-hook\.js"/);
  const stopSection = requirements.slice(requirements.indexOf('[[hooks.Stop]]'));
  assert.ok(stopSection.includes('turn-notify-hook.js'), 'the notification is a Stop hook');

  const { hooksConfig } = require('../../cursor/hooks-config.js');
  const cursor = JSON.parse(hooksConfig('/opt/session-agent'));
  assert.ok(cursor.hooks.stop.some(h => h.command === 'node /opt/session-agent/common/turn-notify-hook.mjs'));
  // The reminder keeps its loop limit; the notification must not continue the turn at all.
  const notify = cursor.hooks.stop.find(h => h.command.includes('turn-notify-hook'));
  assert.equal(notify.loop_limit, undefined);
});
