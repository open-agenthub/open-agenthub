'use strict';

// Pins the chat-relay contract of the Codex Stop hook against the real pinned CLI, the way
// codex-policy-hook-smoke.js pins the PreToolUse deny contract. Runs `codex exec` twice
// against a fake model that answers with one message, once per AGENTHUB_MODE:
//
//   interactive  the managed Stop hook posts {event:"question", message:<the answer>} to
//                /internal/sessions/{id}/notify with the callback token — this is what opens
//                the session's Slack/Telegram/Signal thread;
//   autonomous   nothing is posted — "finished" follows the process, and a question there
//                would open a thread for an answer nobody can give.
//
// What a CLI bump can silently break, and this would notice: the Stop event no longer
// reaching managed hooks, `last_assistant_message` renamed or dropped (the hook would still
// post, but the generic text), or the hook's environment no longer inheriting AGENTHUB_*.
// `transcript_path` must stay set for the user's thread: the hook treats a Codex Stop
// without one as the TUI's title-generation helper and stays silent.

const { spawn, spawnSync } = require('node:child_process');
const { createServer } = require('node:http');
const fs = require('node:fs');

const workspace = '/workspace';
const codexHome = '/tmp/codex-notify-home';
const answer = 'Notify smoke: which branch should I use?';

function sse(events) {
  return events.map(event => `event: ${event.type}\ndata: ${JSON.stringify(event)}\n\n`).join('');
}

async function runOnce(mode) {
  fs.rmSync(codexHome, { recursive: true, force: true });
  fs.mkdirSync(codexHome, { recursive: true });
  const gitInit = spawnSync('git', ['init', '-q', workspace], { encoding: 'utf8' });
  if (gitInit.status !== 0)
    throw new Error(`git init failed: ${gitInit.stderr.slice(0, 300)}`);
  fs.writeFileSync(`${codexHome}/config.toml`, [
    'cli_auth_credentials_store = "file"',
    `[projects."${workspace}"]`,
    'trust_level = "trusted"',
    ''
  ].join('\n'));

  const notifications = [];
  const server = createServer((request, response) => {
    let body = '';
    request.on('data', chunk => { body += chunk; });
    request.on('end', () => {
      if (request.url.endsWith('/notify')) {
        notifications.push({ token: request.headers['x-agent-token'], body: JSON.parse(body) });
        response.writeHead(204);
        response.end();
        return;
      }
      if (request.url === '/v1/responses' && request.method === 'POST' && body.length > 0) {
        response.writeHead(200, { 'Content-Type': 'text/event-stream' });
        response.end(sse([
          { type: 'response.created', response: { id: 'resp-1' } },
          {
            type: 'response.output_item.done',
            item: { type: 'message', role: 'assistant', id: 'msg-1',
              content: [{ type: 'output_text', text: answer }] }
          },
          {
            type: 'response.completed',
            response: { id: 'resp-1', usage: { input_tokens: 0, input_tokens_details: null,
              output_tokens: 0, output_tokens_details: null, total_tokens: 0 } }
          }
        ]));
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
    // Same provider pinning as the policy smoke: keep the exchange on plain HTTP.
    '-c', 'model_provider="smoke"',
    '-c', 'model_providers.smoke.name="smoke"',
    '-c', `model_providers.smoke.base_url="http://127.0.0.1:${port}/v1"`,
    '-c', 'model_providers.smoke.wire_api="responses"',
    '-c', 'model_providers.smoke.env_key="CODEX_API_KEY"',
    '-c', 'model_providers.smoke.supports_websockets=false',
    '-m', 'gpt-5.4', 'Say something.'
  ], {
    cwd: workspace,
    env: {
      ...process.env,
      CODEX_HOME: codexHome,
      CODEX_API_KEY: 'synthetic-notify-smoke-key',
      AGENTHUB_CALLBACK_URL: `http://127.0.0.1:${port}/internal/sessions/notify-smoke`,
      AGENTHUB_CALLBACK_TOKEN: 'smoke-callback-token',
      AGENTHUB_MODE: mode
    },
    stdio: ['ignore', 'pipe', 'pipe']
  });
  let stderr = '';
  child.stderr.on('data', chunk => { stderr += chunk; });
  const exitCode = await new Promise((resolve, reject) => {
    child.on('error', reject);
    child.on('close', resolve);
  });
  await new Promise(resolve => server.close(resolve));
  if (exitCode !== 0)
    throw new Error(`Codex notify smoke (${mode}) exited ${exitCode}: ${stderr.slice(0, 500)}`);
  return notifications;
}

async function main() {
  const interactive = await runOnce('interactive');
  if (interactive.length !== 1)
    throw new Error(`interactive: expected one /notify request, got ${interactive.length}`);
  if (interactive[0].token !== 'smoke-callback-token')
    throw new Error('interactive: notify hook omitted callback authentication');
  if (interactive[0].body.event !== 'question' || interactive[0].body.message !== answer)
    throw new Error(`interactive: unexpected notify body ${JSON.stringify(interactive[0].body)}`);

  const autonomous = await runOnce('autonomous');
  if (autonomous.length !== 0)
    throw new Error(`autonomous: expected no /notify request, got ${autonomous.length}`);

  process.stdout.write('Pinned Codex Stop-hook notify contract passed\n');
}

main().catch(error => {
  process.stderr.write(`${error.message}\n`);
  process.exitCode = 1;
});
