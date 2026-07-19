'use strict';

const { spawn, spawnSync } = require('node:child_process');
const { createServer } = require('node:http');
const fs = require('node:fs');

const workspace = '/workspace';
const codexHome = '/tmp/codex-policy-home';
const blockedMarker = `${workspace}/codex-policy-command-ran`;
const projectHookMarker = `${workspace}/project-hook-ran`;
const command = `printf executed > ${blockedMarker}`;

function sse(events) {
  return events.map(event => `event: ${event.type}\ndata: ${JSON.stringify(event)}\n\n`).join('');
}

function responseCreated(id) {
  return { type: 'response.created', response: { id } };
}

function responseCompleted(id) {
  return {
    type: 'response.completed',
    response: {
      id,
      usage: {
        input_tokens: 0, input_tokens_details: null,
        output_tokens: 0, output_tokens_details: null, total_tokens: 0
      }
    }
  };
}

async function main() {
  fs.rmSync(blockedMarker, { force: true });
  fs.rmSync(projectHookMarker, { force: true });
  fs.rmSync(codexHome, { recursive: true, force: true });
  fs.mkdirSync(codexHome, { recursive: true });
  fs.mkdirSync(`${workspace}/.codex`, { recursive: true });
  const gitInit = spawnSync('git', ['init', '-q', workspace], { encoding: 'utf8' });
  if (gitInit.status !== 0)
    throw new Error(`git init failed: ${gitInit.stderr.slice(0, 300)}`);
  fs.writeFileSync(`${codexHome}/config.toml`, [
    'cli_auth_credentials_store = "file"',
    `[projects."${workspace}"]`,
    'trust_level = "trusted"',
    ''
  ].join('\n'));
  fs.writeFileSync(`${workspace}/.codex/config.toml`, '[features]\nhooks = false\n');
  fs.writeFileSync(`${workspace}/.codex/hooks.json`, JSON.stringify({
    hooks: {
      PreToolUse: [{
        matcher: '*',
        hooks: [{ type: 'command', command: `touch ${projectHookMarker}` }]
      }]
    }
  }));

  const policyRequests = [];
  const responseRequests = [];
  const requestLog = [];
  let responseNumber = 0;
  const server = createServer((request, response) => {
    let body = '';
    request.on('data', chunk => { body += chunk; });
    request.on('end', () => {
      if (request.url.endsWith('/agent-policy')) {
        policyRequests.push({ token: request.headers['x-agent-token'], body: JSON.parse(body) });
        response.writeHead(200, { 'Content-Type': 'application/json' });
        response.end('{"decision":"deny","reason":"fixture deny"}');
        return;
      }
      if (request.url === '/v1/responses') {
        requestLog.push({ method: request.method, bytes: body.length,
          encoding: request.headers['content-encoding'] || '' });
        if (request.method !== 'POST' || body.length === 0) {
          response.writeHead(405);
          response.end();
          return;
        }
        responseRequests.push(JSON.parse(body));
        responseNumber++;
        const events = responseNumber === 1
          ? [
              responseCreated('resp-1'),
              {
                type: 'response.output_item.done',
                item: {
                  type: 'function_call', call_id: 'policy-smoke-call',
                  name: 'shell_command', arguments: JSON.stringify({ command })
                }
              },
              responseCompleted('resp-1')
            ]
          : [
              responseCreated('resp-2'),
              {
                type: 'response.output_item.done',
                item: {
                  type: 'message', role: 'assistant', id: 'msg-1',
                  content: [{ type: 'output_text', text: 'policy smoke complete' }]
                }
              },
              responseCompleted('resp-2')
            ];
        response.writeHead(200, { 'Content-Type': 'text/event-stream' });
        response.end(sse(events));
        return;
      }
      response.writeHead(404);
      response.end();
    });
  });

  await new Promise((resolve, reject) => {
    server.once('error', reject);
    server.listen(0, '127.0.0.1', resolve);
  });
  const port = server.address().port;
  const child = spawn('codex', [
    'exec', '--sandbox', 'workspace-write', '--json', '--dangerously-bypass-hook-trust',
    '--disable', 'enable_request_compression',
    '-c', `openai_base_url="http://127.0.0.1:${port}/v1"`,
    '-m', 'gpt-5.4', 'Run the requested policy smoke command.'
  ], {
    cwd: workspace,
    env: {
      ...process.env,
      CODEX_HOME: codexHome,
      CODEX_API_KEY: 'synthetic-policy-smoke-key',
      AGENTHUB_CALLBACK_URL: `http://127.0.0.1:${port}/internal/sessions/policy-smoke`,
      AGENTHUB_CALLBACK_TOKEN: 'smoke-callback-token',
      AGENTHUB_MODE: 'autonomous'
    },
    stdio: ['ignore', 'pipe', 'pipe']
  });
  let stdout = '';
  let stderr = '';
  child.stdout.on('data', chunk => { stdout += chunk; });
  child.stderr.on('data', chunk => { stderr += chunk; });
  const exitCode = await new Promise((resolve, reject) => {
    child.on('error', reject);
    child.on('close', resolve);
  });
  await new Promise(resolve => server.close(resolve));

  if (exitCode !== 0)
    throw new Error(`Codex hook smoke exited ${exitCode}: ${stderr.slice(0, 500)} requests=${JSON.stringify(requestLog)}`);
  if (policyRequests.length !== 1) throw new Error(`expected one policy hook request, got ${policyRequests.length}`);
  if (policyRequests[0].token !== 'smoke-callback-token') throw new Error('policy hook omitted callback authentication');
  if (policyRequests[0].body.tool !== 'Bash' || policyRequests[0].body.input.command !== command)
    throw new Error('policy hook emitted unexpected pinned input contract');
  if (fs.existsSync(blockedMarker)) throw new Error('denied shell command executed');
  if (fs.existsSync(projectHookMarker)) throw new Error('repository hook overrode managed-only policy');
  if (responseRequests.length !== 2) throw new Error(`expected two model requests, got ${responseRequests.length}`);
  const followup = JSON.stringify(responseRequests[1]);
  if (!followup.includes('blocked by PreToolUse hook')) throw new Error('Codex did not report a PreToolUse denial');
  if ((stdout + stderr).includes('smoke-callback-token')) throw new Error('callback token leaked to Codex output');
  process.stdout.write('Pinned Codex PreToolUse deny contract passed\n');
}

main().catch(error => {
  process.stderr.write(`${error.message}\n`);
  process.exitCode = 1;
});
