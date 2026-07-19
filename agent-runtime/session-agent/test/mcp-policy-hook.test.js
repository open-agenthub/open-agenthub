'use strict';

const assert = require('node:assert/strict');
const { createServer } = require('node:http');
const fs = require('node:fs');
const os = require('node:os');
const path = require('node:path');
const { spawn } = require('node:child_process');
const test = require('node:test');

const runtimeDir = path.join(__dirname, '..', '..');
const sessionAgentDir = path.join(__dirname, '..');
const bashPath = process.platform === 'win32'
  ? 'C:\\Program Files\\Git\\bin\\bash.exe'
  : 'bash';
const hookPath = path.join('..', 'claude', 'hooks', 'mcp-policy-hook.sh');

function runtimeEnvironment(environment) {
  return {
    ...process.env,
    ...environment,
    ...(process.platform === 'win32' ? {
      AGENTHUB_NODE_BIN: '/c/Program Files/nodejs/node.exe',
      AGENTHUB_CURL_BIN: '/c/Windows/System32/curl.exe'
    } : {})
  };
}

function runHook(input, environment = {}) {
  return new Promise((resolve, reject) => {
    const child = spawn(bashPath, [hookPath], {
      cwd: sessionAgentDir,
      env: runtimeEnvironment(environment),
      stdio: ['pipe', 'pipe', 'pipe']
    });
    let stdout = '';
    let stderr = '';
    const timeout = setTimeout(() => {
      child.kill();
      reject(new Error(`hook timed out; stderr: ${stderr}`));
    }, 5000);

    child.stdout.on('data', chunk => { stdout += chunk; });
    child.stderr.on('data', chunk => { stderr += chunk; });
    child.on('error', error => {
      clearTimeout(timeout);
      reject(error);
    });
    child.on('close', (code, signal) => {
      clearTimeout(timeout);
      let output;
      try {
        output = JSON.parse(stdout);
      } catch (error) {
        reject(new Error(`invalid hook output: ${stdout}; stderr: ${stderr}`, { cause: error }));
        return;
      }
      resolve({ code, signal, output, stderr });
    });
    child.stdin.end(JSON.stringify(input));
  });
}

function startPolicyServer(response) {
  const requests = [];
  const server = createServer((request, responseStream) => {
    let body = '';
    request.on('data', chunk => { body += chunk; });
    request.on('end', () => {
      requests.push({
        method: request.method,
        path: request.url,
        token: request.headers['x-agent-token'],
        body: JSON.parse(body)
      });
      responseStream.writeHead(response.status || 200, { 'Content-Type': 'application/json' });
      responseStream.end(request.url === '/mcp-policy' ? response.body : '{}');
    });
  });

  return new Promise((resolve, reject) => {
    server.once('error', reject);
    server.listen(0, process.platform === 'win32' ? '0.0.0.0' : '127.0.0.1', () => {
      const { port } = server.address();
      const host = '127.0.0.1';
      resolve({
        requests,
        url: `http://${host}:${port}`,
        close: () => new Promise(closeResolve => server.close(closeResolve))
      });
    });
  });
}

function createApprovalHook() {
  const directory = fs.mkdtempSync(path.join(os.tmpdir(), 'agenthub-approval-'));
  const scriptPath = path.join(directory, 'approval-hook.sh');
  fs.writeFileSync(scriptPath, `#!/usr/bin/env bash
payload="$(cat)"
"\${AGENTHUB_CURL_BIN:-curl}" -fsS -X POST \\
  -H "X-Agent-Token: \${AGENTHUB_CALLBACK_TOKEN:-}" \\
  -H 'Content-Type: application/json' \\
  --data "$payload" "\${AGENTHUB_CALLBACK_URL}/permission" >/dev/null
printf '%s\\n' '{"hookSpecificOutput":{"hookEventName":"PreToolUse","permissionDecision":"ask","permissionDecisionReason":"approval"}}'
`);
  fs.chmodSync(scriptPath, 0o755);
  return process.platform === 'win32'
    ? `/${scriptPath[0].toLowerCase()}${scriptPath.slice(2).replaceAll('\\', '/')}`
    : scriptPath;
}

function renderSettings(mode) {
  const result = require('node:child_process').spawnSync(
    bashPath,
    [hookPath, '--settings'],
    {
      encoding: 'utf8',
      cwd: sessionAgentDir,
      env: runtimeEnvironment({
        AGENTHUB_MODE: mode,
        AGENTHUB_RUNTIME: '/opt/session-agent/claude/hooks'
      })
    }
  );
  assert.equal(result.status, 0, result.stderr);
  return JSON.parse(result.stdout);
}

async function closedServerUrl() {
  const server = await startPolicyServer({ body: JSON.stringify({ restricted: false, decision: 'allow' }) });
  await server.close();
  return server.url;
}

test('MCP policy denies a blocked MCP tool', async () => {
  const server = await startPolicyServer({
    body: JSON.stringify({ restricted: true, decision: 'deny' })
  });

  try {
    const result = await runHook(
      { tool_name: 'mcp__server__tool' },
      { AGENTHUB_CALLBACK_URL: server.url, AGENTHUB_CALLBACK_TOKEN: 'callback-token' }
    );

    assert.equal(result.output.hookSpecificOutput.permissionDecision, 'deny');
    assert.equal(result.output.hookSpecificOutput.permissionDecisionReason,
      'Blocked by the session MCP sharing policy');
    assert.deepEqual(server.requests, [{
      method: 'POST',
      path: '/mcp-policy',
      token: 'callback-token',
      body: { tool: 'mcp__server__tool' }
    }]);
  } finally {
    await server.close();
  }
});

test('MCP policy preserves normal flow for an unrestricted response', async () => {
  const server = await startPolicyServer({
    body: JSON.stringify({ restricted: false, decision: 'allow' })
  });

  try {
    const result = await runHook(
      { tool_name: 'mcp__server__tool' },
      { AGENTHUB_CALLBACK_URL: server.url, AGENTHUB_CALLBACK_TOKEN: 'callback-token' }
    );

    assert.equal(result.output.hookSpecificOutput, undefined);
    assert.equal(server.requests[0].body.tool, 'mcp__server__tool');
  } finally {
    await server.close();
  }
});

test('MCP policy fails closed when the endpoint fails and a policy is configured', async () => {
  const result = await runHook(
    { tool_name: 'mcp__server__tool' },
    {
      AGENTHUB_CALLBACK_URL: await closedServerUrl(),
      AGENTHUB_CALLBACK_TOKEN: 'callback-token',
      AGENTHUB_MCP_POLICY: '1'
    }
  );

  assert.equal(result.output.hookSpecificOutput.permissionDecision, 'deny');
  assert.equal(result.output.hookSpecificOutput.permissionDecisionReason,
    'Blocked by the session MCP sharing policy');
});

test('MCP policy preserves normal flow when the endpoint fails without a policy', async () => {
  const result = await runHook(
    { tool_name: 'mcp__server__tool' },
    {
      AGENTHUB_CALLBACK_URL: await closedServerUrl(),
      AGENTHUB_CALLBACK_TOKEN: 'callback-token'
    }
  );

  assert.equal(result.output.hookSpecificOutput, undefined);
});

test('MCP policy ignores non-MCP tools', async () => {
  const result = await runHook(
    { tool_name: 'Read' },
    {
      AGENTHUB_CALLBACK_URL: 'http://127.0.0.1:1',
      AGENTHUB_CALLBACK_TOKEN: 'callback-token',
      AGENTHUB_MCP_POLICY: '1'
    }
  );

  assert.deepEqual(result.output, {});
});

test('MCP policy denies before interactive approval can be requested', async () => {
  const server = await startPolicyServer({
    body: JSON.stringify({ restricted: true, decision: 'deny' })
  });

  try {
    const result = await runHook(
      { tool_name: 'mcp__server__tool' },
      {
        AGENTHUB_CALLBACK_URL: server.url,
        AGENTHUB_CALLBACK_TOKEN: 'callback-token',
        AGENTHUB_MODE: 'interactive',
        AGENTHUB_APPROVAL_HOOK: createApprovalHook()
      }
    );

    assert.equal(result.output.hookSpecificOutput.permissionDecision, 'deny');
    assert.deepEqual(server.requests.map(request => request.path), ['/mcp-policy']);
  } finally {
    await server.close();
  }
});

test('MCP policy delegates allowed interactive calls to approval after policy', async () => {
  const server = await startPolicyServer({
    body: JSON.stringify({ restricted: true, decision: 'allow' })
  });

  try {
    const result = await runHook(
      { tool_name: 'mcp__server__tool' },
      {
        AGENTHUB_CALLBACK_URL: server.url,
        AGENTHUB_CALLBACK_TOKEN: 'callback-token',
        AGENTHUB_MODE: 'interactive',
        AGENTHUB_APPROVAL_HOOK: createApprovalHook()
      }
    );

    assert.equal(result.output.hookSpecificOutput.permissionDecision, 'ask');
    assert.deepEqual(server.requests.map(request => request.path), ['/mcp-policy', '/permission']);
  } finally {
    await server.close();
  }
});

test('MCP policy settings use deterministic MCP and built-in matchers interactively', () => {
  const settings = renderSettings('interactive');

  assert.equal(settings.hooks.Notification.length, 1);
  assert.deepEqual(settings.hooks.PreToolUse, [
    {
      matcher: 'mcp__.*',
      hooks: [{
        type: 'command',
        command: '/opt/session-agent/claude/hooks/mcp-policy-hook.sh',
        timeout: 300
      }]
    },
    {
      matcher: '^(?!mcp__).*',
      hooks: [{
        type: 'command',
        command: '/opt/session-agent/claude/hooks/pretooluse-hook.sh',
        timeout: 300
      }]
    }
  ]);
});

test('MCP policy settings register only MCP tools for non-interactive modes', () => {
  for (const mode of ['autonomous', 'scheduled']) {
    const settings = renderSettings(mode);
    assert.deepEqual(settings.hooks.PreToolUse, [{
      matcher: 'mcp__.*',
      hooks: [{
        type: 'command',
        command: '/opt/session-agent/claude/hooks/mcp-policy-hook.sh',
        timeout: 5
      }]
    }]);
  }
});

test('MCP policy hook is copied into the Claude runtime image and made executable', () => {
  const dockerfile = fs.readFileSync(path.join(runtimeDir, 'claude', 'Dockerfile'), 'utf8');

  assert.ok(dockerfile.includes('COPY claude /opt/session-agent/claude'));
  assert.ok(dockerfile.includes('/opt/session-agent/claude/hooks/mcp-policy-hook.sh'));
});
