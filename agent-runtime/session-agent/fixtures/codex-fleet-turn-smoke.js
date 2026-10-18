'use strict';

// Pins the next-turn contract of the managed fleet-turn hook against the real pinned CLI
// (docs/priority-messages.md, #173), the way codex-turn-notify-smoke.js pins the relay's Stop
// hook. Runs `codex exec` against a fake model and a stand-in session agent on AGENTHUB_PORT:
//
//   - UserPromptSubmit reaches the hook, which reports {event:"prompt"};
//   - the Stop hook reports {event:"stop"}, and the text the session agent answers with is the
//     model's very next input — that is how a fleet message becomes the agent's next turn;
//   - the continuation's own Stop is reported again and, answered with nothing, ends the run.
//
// What a CLI bump can silently break, and this would notice: Stop no longer taking
// {"decision":"block","reason"} as a continuation, UserPromptSubmit no longer reaching managed
// hooks, or the hook's environment losing AGENTHUB_*. Every one of them leaves fleet messages
// waiting for agent_inbox again without any other symptom. The hook only acts in interactive
// sessions; exec stands in for the TUI here, so AGENTHUB_MODE is set to interactive.

const { spawn, spawnSync } = require('node:child_process');
const { createServer } = require('node:http');
const fs = require('node:fs');
const os = require('node:os');
const path = require('node:path');

const workspace = '/workspace';
const fleetText = '[AgentHub message from agent "Smoke Peer" (peer-1)]\nFLEET-SMOKE-7: please also run the linter.';

function sse(events) {
  return events.map(event => `event: ${event.type}\ndata: ${JSON.stringify(event)}\n\n`).join('');
}

function lastInput(body) {
  const items = Array.isArray(body.input) ? body.input : [];
  const last = [...items].reverse().find(item => item.type === 'message' && item.role !== 'assistant');
  return last ? JSON.stringify(last.content) : '';
}

function listen(server) {
  return new Promise((resolve, reject) => {
    server.once('error', reject);
    server.listen(0, '127.0.0.1', () => resolve(server.address().port));
  });
}

async function main() {
  const codexHome = fs.mkdtempSync(path.join(os.tmpdir(), 'codex-fleet-home-'));
  const gitInit = spawnSync('git', ['init', '-q', workspace], { encoding: 'utf8' });
  if (gitInit.status !== 0) throw new Error(`git init failed: ${gitInit.stderr.slice(0, 300)}`);
  fs.writeFileSync(`${codexHome}/config.toml`, [
    'cli_auth_credentials_store = "file"', `[projects."${workspace}"]`, 'trust_level = "trusted"', ''
  ].join('\n'));

  const events = [];
  const inputs = [];
  const sessionAgent = createServer((request, response) => {
    let body = '';
    request.on('data', chunk => { body += chunk; });
    request.on('end', () => {
      const event = request.url === '/agenthub/turn' && request.headers['x-agent-token'] === 'smoke-token'
        ? JSON.parse(body).event : `bad request ${request.url}`;
      events.push(event);
      const first = event === 'stop' && events.filter(e => e === 'stop').length === 1;
      response.writeHead(200, { 'Content-Type': 'application/json' });
      response.end(JSON.stringify(first ? { text: fleetText } : {}));
    });
  });
  const model = createServer((request, response) => {
    let body = '';
    request.on('data', chunk => { body += chunk; });
    request.on('end', () => {
      if (request.url !== '/v1/responses' || request.method !== 'POST') {
        response.writeHead(404);
        response.end();
        return;
      }
      inputs.push(lastInput(JSON.parse(body)));
      const n = inputs.length;
      response.writeHead(200, { 'Content-Type': 'text/event-stream' });
      response.end(sse([
        { type: 'response.created', response: { id: `resp-${n}` } },
        { type: 'response.output_item.done', item: { type: 'message', role: 'assistant', id: `msg-${n}`,
          content: [{ type: 'output_text', text: `Answer ${n}.` }] } },
        { type: 'response.completed', response: { id: `resp-${n}`, usage: { input_tokens: 0,
          input_tokens_details: null, output_tokens: 0, output_tokens_details: null, total_tokens: 0 } } }
      ]));
    });
  });
  const agentPort = await listen(sessionAgent);
  const modelPort = await listen(model);

  const child = spawn('codex', [
    'exec', '--sandbox', 'workspace-write', '--json', '--dangerously-bypass-hook-trust',
    '--disable', 'enable_request_compression',
    '-c', 'model_provider="smoke"',
    '-c', 'model_providers.smoke.name="smoke"',
    '-c', `model_providers.smoke.base_url="http://127.0.0.1:${modelPort}/v1"`,
    '-c', 'model_providers.smoke.wire_api="responses"',
    '-c', 'model_providers.smoke.env_key="CODEX_API_KEY"',
    '-c', 'model_providers.smoke.supports_websockets=false',
    '-m', 'gpt-5.4', 'Say something.'
  ], {
    cwd: workspace,
    env: {
      ...process.env,
      CODEX_HOME: codexHome,
      CODEX_API_KEY: 'synthetic-fleet-smoke-key',
      AGENTHUB_CALLBACK_TOKEN: 'smoke-token',
      AGENTHUB_PORT: String(agentPort),
      AGENTHUB_MODE: 'interactive'
    },
    stdio: ['ignore', 'pipe', 'pipe']
  });
  let stderr = '';
  child.stderr.on('data', chunk => { stderr += chunk; });
  const exitCode = await new Promise((resolve, reject) => {
    child.on('error', reject);
    child.on('close', resolve);
  });
  sessionAgent.close();
  model.close();
  if (exitCode !== 0) throw new Error(`Codex fleet-turn smoke exited ${exitCode}: ${stderr.slice(0, 500)}`);

  if (events[0] !== 'prompt') throw new Error(`expected the prompt to be reported first, got ${JSON.stringify(events)}`);
  if (events.filter(e => e === 'stop').length !== 2)
    throw new Error(`expected the turn and its continuation to stop, got ${JSON.stringify(events)}`);
  if (inputs.length !== 2 || !inputs[1].includes('FLEET-SMOKE-7'))
    throw new Error(`the fleet message was not the model's next input: ${JSON.stringify(inputs)}`);
  if (inputs[0].includes('FLEET-SMOKE-7')) throw new Error('the fleet message reached the first request');

  process.stdout.write('Pinned Codex fleet next-turn hook contract passed\n');
}

main().catch(error => {
  process.stderr.write(`${error.message}\n`);
  process.exitCode = 1;
});
