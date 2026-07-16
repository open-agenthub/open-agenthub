'use strict';

const assert = require('node:assert/strict');
const { createServer } = require('node:http');
const path = require('node:path');
const { spawn } = require('node:child_process');
const test = require('node:test');

const hookPath = './mcp-policy-hook.sh';

function runHook(input, environment = {}) {
  return new Promise((resolve, reject) => {
    const child = process.platform === 'win32'
      ? spawn('bash', [
        '-c',
        'export PATH="/mnt/c/Program Files/nodejs:$PATH"; exec ./mcp-policy-hook.sh'
      ], {
        cwd: path.join(__dirname, '..'),
        env: {
          ...process.env,
          ...environment,
          ...(process.platform === 'win32' ? { AGENTHUB_NODE_BIN: '/mnt/c/Program Files/nodejs/node.exe', AGENTHUB_CURL_BIN: '/mnt/c/WINDOWS/system32/curl.exe' } : {})
        },
        stdio: ['pipe', 'pipe', 'pipe']
      })
      : spawn('bash', [hookPath], {
        cwd: path.join(__dirname, '..'),
        env: {
          ...process.env,
          ...environment,
          ...(process.platform === 'win32' ? { AGENTHUB_NODE_BIN: '/mnt/c/Program Files/nodejs/node.exe', AGENTHUB_CURL_BIN: '/mnt/c/WINDOWS/system32/curl.exe' } : {})
        },
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
      responseStream.end(response.body);
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
