'use strict';

const assert = require('node:assert/strict');
const { spawn } = require('node:child_process');
const { createServer } = require('node:http');
const fs = require('node:fs');
const path = require('node:path');
const test = require('node:test');

const hookPath = path.join(__dirname, '..', '..', 'codex', 'policy-hook.js');
const requirementsPath = path.join(__dirname, '..', '..', 'codex', 'requirements.toml');

function runHook(input, environment = {}) {
  return new Promise((resolve, reject) => {
    const child = spawn(process.execPath, [hookPath], {
      env: { ...process.env, AGENTHUB_CALLBACK_TOKEN: 'callback-secret', ...environment },
      stdio: ['pipe', 'pipe', 'pipe']
    });
    let stdout = '';
    let stderr = '';
    const timeout = setTimeout(() => {
      child.kill();
      reject(new Error('hook timed out'));
    }, 5000);
    child.stdout.on('data', chunk => { stdout += chunk; });
    child.stderr.on('data', chunk => { stderr += chunk; });
    child.on('error', reject);
    child.on('close', code => {
      clearTimeout(timeout);
      resolve({ code, stdout, stderr, output: stdout ? JSON.parse(stdout) : null });
    });
    child.stdin.end(typeof input === 'string' ? input : JSON.stringify(input));
  });
}

function startServer(responses) {
  const requests = [];
  const server = createServer((request, response) => {
    let body = '';
    request.on('data', chunk => { body += chunk; });
    request.on('end', () => {
      requests.push({ path: request.url, method: request.method, token: request.headers['x-agent-token'], body });
      const reply = responses.shift() ?? { status: 500, body: '{}' };
      response.writeHead(reply.status ?? 200, { 'Content-Type': 'application/json' });
      response.end(reply.body);
    });
  });
  return new Promise((resolve, reject) => {
    server.once('error', reject);
    server.listen(0, '127.0.0.1', () => resolve({
      url: `http://127.0.0.1:${server.address().port}`,
      requests,
      close: () => new Promise(done => server.close(done))
    }));
  });
}

function preTool(tool = 'Bash', input = { command: 'git status' }) {
  return { hook_event_name: 'PreToolUse', tool_name: tool, tool_input: input };
}

test('PreToolUse posts only tool/input and emits the pinned Codex deny shape', async () => {
  const server = await startServer([{ body: '{"decision":"deny","reason":"do not echo command"}' }]);
  try {
    const result = await runHook(preTool(), {
      AGENTHUB_CALLBACK_URL: server.url,
      AGENTHUB_MODE: 'autonomous'
    });
    assert.equal(result.code, 0);
    assert.deepEqual(result.output, {
      hookSpecificOutput: {
        hookEventName: 'PreToolUse',
        permissionDecision: 'deny',
        permissionDecisionReason: 'Blocked by the session policy.'
      }
    });
    assert.deepEqual(JSON.parse(server.requests[0].body), {
      tool: 'Bash', input: { command: 'git status' }
    });
    assert.equal(server.requests[0].path, '/agent-policy');
    assert.equal(server.requests[0].token, 'callback-secret');
    assert.doesNotMatch(result.stdout + result.stderr, /callback-secret|git status|do not echo command/);
  } finally {
    await server.close();
  }
});

test('allowed PreToolUse exits silently so Codex continues', async () => {
  const server = await startServer([{ body: '{"decision":"allow","reason":"ok"}' }]);
  try {
    const result = await runHook(preTool(), { AGENTHUB_CALLBACK_URL: server.url, AGENTHUB_MODE: 'scheduled' });
    assert.equal(result.code, 0);
    assert.equal(result.stdout, '');
    assert.equal(result.stderr, '');
  } finally {
    await server.close();
  }
});

test('interactive ask and callback failure preserve normal Codex permission flow', async () => {
  const server = await startServer([{ body: '{"decision":"ask","reason":"interactive"}' }]);
  try {
    const ask = await runHook(preTool(), { AGENTHUB_CALLBACK_URL: server.url, AGENTHUB_MODE: 'interactive' });
    assert.equal(ask.stdout, '');
  } finally {
    await server.close();
  }
  const failed = await runHook(preTool(), {
    AGENTHUB_CALLBACK_URL: 'http://127.0.0.1:1', AGENTHUB_MODE: 'interactive'
  });
  assert.equal(failed.stdout, '');
  assert.equal(failed.stderr, '');
});

test('non-interactive callback and malformed-response failures fail closed', async () => {
  const unavailable = await runHook(preTool(), {
    AGENTHUB_CALLBACK_URL: 'http://127.0.0.1:1', AGENTHUB_MODE: 'autonomous'
  });
  assert.equal(unavailable.output.hookSpecificOutput.permissionDecision, 'deny');

  const server = await startServer([{ body: '{"decision":"ALLOW"}' }]);
  try {
    const malformed = await runHook(preTool(), {
      AGENTHUB_CALLBACK_URL: server.url, AGENTHUB_MODE: 'scheduled'
    });
    assert.equal(malformed.output.hookSpecificOutput.permissionDecision, 'deny');
  } finally {
    await server.close();
  }
});

test('oversized or malformed hook input is bounded and fails closed without leaking it', async () => {
  const oversized = `{"secret":"${'x'.repeat(300_000)}"}`;
  const result = await runHook(oversized, { AGENTHUB_MODE: 'autonomous' });
  assert.equal(result.output.hookSpecificOutput.permissionDecision, 'deny');
  assert.ok(result.stdout.length < 300);
  assert.equal(result.stderr, '');
});

test('PermissionRequest checks policy before contacting out-of-band approval', async () => {
  const server = await startServer([{ body: '{"decision":"deny"}' }]);
  try {
    const result = await runHook({
      hook_event_name: 'PermissionRequest', tool_name: 'mcp__docs__search', tool_input: { query: 'secret' }
    }, { AGENTHUB_CALLBACK_URL: server.url, AGENTHUB_MODE: 'interactive' });
    assert.deepEqual(result.output, {
      hookSpecificOutput: {
        hookEventName: 'PermissionRequest',
        decision: { behavior: 'deny', message: 'Blocked by the session policy.' }
      }
    });
    assert.deepEqual(server.requests.map(request => request.path), ['/agent-policy']);
  } finally {
    await server.close();
  }
});

test('non-interactive PermissionRequest emits allow after policy approval', async () => {
  const server = await startServer([{ body: '{"decision":"allow"}' }]);
  try {
    const result = await runHook({
      hook_event_name: 'PermissionRequest', tool_name: 'Bash',
      tool_input: { command: 'git status' }
    }, { AGENTHUB_CALLBACK_URL: server.url, AGENTHUB_MODE: 'autonomous' });
    assert.deepEqual(result.output, {
      hookSpecificOutput: {
        hookEventName: 'PermissionRequest', decision: { behavior: 'allow' }
      }
    });
    assert.deepEqual(server.requests.map(request => request.path), ['/agent-policy']);
  } finally {
    await server.close();
  }
});

test('PermissionRequest polls bounded out-of-band approval and maps allow', async () => {
  const server = await startServer([
    { body: '{"decision":"ask"}' },
    { body: '{"id":"request-1"}' },
    { body: '{"decision":"pending"}' },
    { body: '{"decision":"allow"}' }
  ]);
  try {
    const result = await runHook({
      hook_event_name: 'PermissionRequest', tool_name: 'Bash', tool_input: { command: 'git push' }
    }, {
      AGENTHUB_CALLBACK_URL: server.url,
      AGENTHUB_MODE: 'interactive',
      AGENTHUB_APPROVAL_POLLS: '2',
      AGENTHUB_APPROVAL_INTERVAL_MS: '10'
    });
    assert.deepEqual(result.output, {
      hookSpecificOutput: {
        hookEventName: 'PermissionRequest', decision: { behavior: 'allow' }
      }
    });
    assert.deepEqual(server.requests.map(request => request.path), [
      '/agent-policy', '/permission', '/permission/request-1', '/permission/request-1'
    ]);
  } finally {
    await server.close();
  }
});

test('system requirements own all tool hooks and exclude repository hooks', () => {
  const requirements = fs.readFileSync(requirementsPath, 'utf8');
  assert.match(requirements, /^allow_managed_hooks_only = true/m);
  assert.match(requirements, /^hooks = true/m);
  assert.match(requirements, /^managed_dir = "\/opt\/session-agent\/codex"/m);
  assert.match(requirements, /\[\[hooks\.PreToolUse\]\][\s\S]*matcher = "\*"/m);
  assert.match(requirements, /\[\[hooks\.PermissionRequest\]\][\s\S]*matcher = "\*"/m);
});
