// Boots the real files/server.mjs as a child process the way an agent CLI does, with RUNTIME
// set the way the entrypoint sets it.
//
// Every other test in files-server.test.js imports the module from this directory with RUNTIME
// unset, so it takes the fallback branch of the runtime-root lookup and resolves every sibling
// module against the checkout. The image has the opposite layout — the runtime root is the
// directory that contains files/ — and a require written relative to the root missed there
// while the suite stayed green: the whole MCP server died at import with MODULE_NOT_FOUND and
// the session simply had no file tools.

const assert = require('node:assert/strict');
const path = require('node:path');
const { spawn } = require('node:child_process');
const test = require('node:test');

const serverPath = path.join(__dirname, '..', '..', 'files', 'server.mjs');

/** Sends one message per line and resolves once the child has exited. */
function boot(env, messages) {
  const child = spawn(process.execPath, [serverPath], {
    env: { ...process.env, ...env },
    stdio: ['pipe', 'pipe', 'pipe']
  });
  const replies = [];
  let buffer = '';
  let stderr = '';
  child.stderr.on('data', chunk => { stderr += chunk; });
  return new Promise((resolve, reject) => {
    child.stdout.on('data', chunk => {
      buffer += chunk;
      let newline;
      while ((newline = buffer.indexOf('\n')) >= 0) {
        const line = buffer.slice(0, newline).trim();
        buffer = buffer.slice(newline + 1);
        if (line) replies.push(JSON.parse(line));
      }
    });
    child.on('error', reject);
    child.on('close', code => resolve({ replies, stderr, code }));
    for (const message of messages) child.stdin.write(`${JSON.stringify(message)}\n`);
    child.stdin.end();
  });
}

// The backend is never reached during initialize; the client only refuses to be constructed
// without them.
const backend = {
  AGENTHUB_CALLBACK_URL: 'http://127.0.0.1:1/internal/sessions/s1',
  AGENTHUB_CALLBACK_TOKEN: 'secret'
};

const initialize = {
  jsonrpc: '2.0', id: 1, method: 'initialize',
  params: { protocolVersion: '2025-06-18', capabilities: {}, clientInfo: { name: 'test', version: '1' } }
};

test('the server answers initialize with RUNTIME pointing somewhere other than files/\'s parent', async () => {
  // Any root that is not the parent of files/ stands in for the image, where RUNTIME is the
  // package root and files/ sits inside it rather than beside it. The directory still has to be
  // one the installed SDK resolves from, which is what rules out a temporary directory here.
  const { replies, stderr, code } = await boot({ ...backend, RUNTIME: __dirname }, [initialize]);

  assert.equal(code, 0, stderr);
  assert.doesNotMatch(stderr, /MODULE_NOT_FOUND/);
  assert.equal(replies[0].result.serverInfo.name, 'agenthub_files');
});

test('the server answers initialize with RUNTIME unset', async () => {
  const { replies, stderr, code } = await boot({ ...backend, RUNTIME: '' }, [initialize]);

  assert.equal(code, 0, stderr);
  assert.equal(replies[0].result.serverInfo.name, 'agenthub_files');
});
