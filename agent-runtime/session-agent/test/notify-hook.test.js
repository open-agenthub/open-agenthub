'use strict';

const assert = require('node:assert/strict');
const { createServer } = require('node:http');
const path = require('node:path');
const { spawn } = require('node:child_process');
const test = require('node:test');

const sessionAgentDir = path.join(__dirname, '..');
const bashPath = process.platform === 'win32'
  ? 'C:\\Program Files\\Git\\bin\\bash.exe'
  : 'bash';
const hookPath = path.join('..', 'claude', 'hooks', 'notify-hook.sh');

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

function startCallbackServer() {
  const requests = [];
  const server = createServer((request, response) => {
    let body = '';
    request.on('data', chunk => { body += chunk; });
    request.on('end', () => {
      requests.push({ path: request.url, body: JSON.parse(body) });
      response.writeHead(204);
      response.end();
    });
  });
  return new Promise(resolve => {
    server.listen(0, '127.0.0.1', () => {
      resolve({
        requests,
        url: `http://127.0.0.1:${server.address().port}`,
        close: () => new Promise(done => server.close(done))
      });
    });
  });
}

function runHook(input, environment) {
  return new Promise((resolve, reject) => {
    const child = spawn(bashPath, [hookPath], {
      cwd: sessionAgentDir,
      env: runtimeEnvironment(environment),
      stdio: ['pipe', 'pipe', 'pipe']
    });
    let stderr = '';
    const timeout = setTimeout(() => {
      child.kill();
      reject(new Error(`hook timed out; stderr: ${stderr}`));
    }, 10000);
    child.stderr.on('data', chunk => { stderr += chunk; });
    child.on('error', error => { clearTimeout(timeout); reject(error); });
    child.on('close', code => { clearTimeout(timeout); resolve({ code, stderr }); });
    child.stdin.end(JSON.stringify(input));
  });
}

test('forwards a genuine waiting-for-input notification', async () => {
  const server = await startCallbackServer();
  try {
    const { code } = await runHook(
      { message: 'Claude is waiting for your input' },
      { AGENTHUB_CALLBACK_URL: server.url, AGENTHUB_CALLBACK_TOKEN: 'tok' }
    );
    assert.equal(code, 0);
    assert.equal(server.requests.length, 1);
    assert.equal(server.requests[0].path, '/notify');
    assert.equal(server.requests[0].body.event, 'question');
    assert.equal(server.requests[0].body.message, 'Claude is waiting for your input');
  } finally {
    await server.close();
  }
});

test('drops tool-permission notifications (PreToolUse flow handles those)', async () => {
  const server = await startCallbackServer();
  try {
    const { code } = await runHook(
      { message: 'Claude needs your permission to use Bash' },
      { AGENTHUB_CALLBACK_URL: server.url, AGENTHUB_CALLBACK_TOKEN: 'tok' }
    );
    assert.equal(code, 0);
    assert.equal(server.requests.length, 0);
  } finally {
    await server.close();
  }
});

test('falls back to a default message when the payload is malformed', async () => {
  const server = await startCallbackServer();
  try {
    const child = spawn(bashPath, [hookPath], {
      cwd: sessionAgentDir,
      env: runtimeEnvironment({ AGENTHUB_CALLBACK_URL: server.url, AGENTHUB_CALLBACK_TOKEN: 'tok' }),
      stdio: ['pipe', 'ignore', 'ignore']
    });
    child.stdin.end('this is not json');
    await new Promise(resolve => child.on('close', resolve));

    assert.equal(server.requests.length, 1);
    assert.equal(server.requests[0].body.message, 'The agent is waiting for your reply.');
  } finally {
    await server.close();
  }
});
