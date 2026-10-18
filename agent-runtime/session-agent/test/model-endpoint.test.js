'use strict';

const assert = require('node:assert/strict');
const fs = require('node:fs');
const http = require('node:http');
const os = require('node:os');
const path = require('node:path');
const { spawnSync } = require('node:child_process');
const test = require('node:test');

const modelEndpoint = require('../../common/model-endpoint');
const { providerToml } = require('../../codex/model-provider');
const userConfig = require('../../opencode/user-config');
const claudeDriver = require('../../claude/driver');
const codexDriver = require('../../codex/driver');
const opencodeDriver = require('../../opencode/driver');

const URL = 'https://llm.example.com/v1';

function endpointEnv(extra = {}) {
  return {
    AGENTHUB_AUTH_MODE: 'customendpoint',
    AGENTHUB_MODEL_ENDPOINT_URL: URL + '/',
    AGENTHUB_MODEL_NAME: 'qwen3-coder-30b',
    AGENTHUB_MODEL_API_KEY: 'sk-synthetic-not-real',
    AGENTHUB_STATE_RESTORED: '0',
    ...extra
  };
}

function reply(status, body = '') {
  return async () => ({ status, text: async () => body });
}

test('Claude Code gets the base without /v1, because it appends /v1/messages itself', () => {
  assert.equal(modelEndpoint.claudeBaseUrl('https://llm.example.com/v1/'), 'https://llm.example.com');
  assert.equal(modelEndpoint.claudeBaseUrl('https://gw.example.com/anthropic'), 'https://gw.example.com/anthropic');
});

test('Claude prepare hands the endpoint to the CLI child only and points every tier at the model', () => {
  const env = endpointEnv({ ANTHROPIC_API_KEY: 'must-not-survive' });

  const { childEnv } = claudeDriver.prepare(env);

  assert.equal(childEnv.ANTHROPIC_BASE_URL, 'https://llm.example.com');
  assert.equal(childEnv.ANTHROPIC_AUTH_TOKEN, 'sk-synthetic-not-real');
  for (const name of ['ANTHROPIC_MODEL', 'ANTHROPIC_DEFAULT_OPUS_MODEL', 'ANTHROPIC_DEFAULT_SONNET_MODEL',
    'ANTHROPIC_DEFAULT_HAIKU_MODEL', 'CLAUDE_CODE_SUBAGENT_MODEL'])
    assert.equal(childEnv[name], 'qwen3-coder-30b', name);
  assert.equal(childEnv.ANTHROPIC_API_KEY, undefined);
  assert.equal(childEnv.API_TIMEOUT_MS, '1800000');
  assert.equal(childEnv.CLAUDE_STREAM_FIRST_BYTE_TIMEOUT_MS, '1800000');
  // The server's env is what every shell the agent opens inherits.
  assert.equal(env.AGENTHUB_MODEL_API_KEY, undefined);
  assert.equal(env.ANTHROPIC_API_KEY, undefined);
});

test('a keyless endpoint still gives Claude Code a token, so it does not ask for a login', () => {
  const { childEnv } = claudeDriver.prepare(endpointEnv({ AGENTHUB_MODEL_API_KEY: '' }));
  assert.equal(childEnv.ANTHROPIC_AUTH_TOKEN, modelEndpoint.KEYLESS_TOKEN);
});

test('Codex and OpenCode prepare scope the endpoint key to the CLI child', () => {
  for (const driver of [codexDriver, opencodeDriver]) {
    const env = endpointEnv();
    assert.deepEqual(driver.prepare(env), { childEnv: { AGENTHUB_MODEL_API_KEY: 'sk-synthetic-not-real' } });
    assert.equal(env.AGENTHUB_MODEL_API_KEY, undefined);
    assert.equal(driver.prepare(endpointEnv({ AGENTHUB_MODEL_API_KEY: '' })), undefined);
  }
});

test('the Codex provider selects the endpoint with the Responses API, before any table', () => {
  const toml = providerToml(endpointEnv());

  assert.match(toml, /^model = "qwen3-coder-30b"\nmodel_provider = "agenthub_endpoint"\n/);
  assert.match(toml, /\[model_providers\.agenthub_endpoint\]/);
  assert.match(toml, /base_url = "https:\/\/llm\.example\.com\/v1"\n/);
  assert.match(toml, /wire_api = "responses"/);
  assert.match(toml, /env_key = "AGENTHUB_MODEL_API_KEY"/);
  assert.match(toml, /stream_idle_timeout_ms = 1800000\n/);
  assert.doesNotMatch(toml, /sk-synthetic-not-real/);
  // Codex fails every request when env_key names an unset variable.
  assert.doesNotMatch(providerToml(endpointEnv({ AGENTHUB_MODEL_API_KEY: '' })), /env_key/);
});

test('a quote in the model id cannot end the TOML value early', () => {
  const toml = providerToml(endpointEnv({ AGENTHUB_MODEL_NAME: 'a"b\\c' }));
  assert.match(toml, /^model = "a\\"b\\\\c"$/m);
});

test('the OpenCode config registers the endpoint as its own provider and keeps the key a reference', () => {
  const config = userConfig.userConfig(endpointEnv({ AGENTHUB_OPENCODE_MODEL: 'opencode-go/glm-5.3' }), {
    listModels: () => { throw new Error('the Go catalogue must not be listed'); }
  });

  assert.equal(config.model, 'agenthub-endpoint/qwen3-coder-30b');
  assert.deepEqual(config.provider, {
    'agenthub-endpoint': {
      npm: '@ai-sdk/openai-compatible', name: 'Custom endpoint',
      options: { baseURL: URL, headerTimeout: 1800000, apiKey: '{env:AGENTHUB_MODEL_API_KEY}' },
      models: { 'qwen3-coder-30b': { name: 'qwen3-coder-30b' } }
    }
  });
  assert.doesNotMatch(JSON.stringify(config), /sk-synthetic-not-real/);
  const keyless = userConfig.userConfig(endpointEnv({ AGENTHUB_MODEL_API_KEY: '' }));
  assert.equal(keyless.provider['agenthub-endpoint'].options.apiKey, undefined);
});

test('the probe fails on a refused key and names the missing credential', async () => {
  const settings = modelEndpoint.settings(endpointEnv({ AGENTHUB_MODEL_API_KEY: '' }));
  const result = await modelEndpoint.probe(settings, 'chat', { fetchImpl: reply(401, '{"error":"Invalid API Key"}') });
  assert.equal(result.ok, false);
  assert.match(result.message, /HTTP 401/);
  assert.match(result.message, /No custom endpoint API key is stored/);
});

test('the probe fails on a missing route and says which API the agent needs', async () => {
  const settings = modelEndpoint.settings(endpointEnv());
  const claude = await modelEndpoint.probe(settings, 'messages', { fetchImpl: reply(404, 'File Not Found') });
  const codex = await modelEndpoint.probe(settings, 'responses', { fetchImpl: reply(404) });

  assert.match(claude.message, /https:\/\/llm\.example\.com\/v1\/messages does not exist \(HTTP 404\)/);
  assert.match(claude.message, /Anthropic Messages API/);
  assert.match(claude.message, /Server said: File Not Found/);
  assert.match(codex.message, /\/v1\/responses does not exist/);
  assert.match(codex.message, /OpenAI Responses API/);
});

test('the probe lets the CLI start on any answer that shows the route exists', async () => {
  const settings = modelEndpoint.settings(endpointEnv());
  // llama-server answers a /v1/messages body without "messages" with a 500.
  for (const status of [200, 400, 422, 500])
    assert.deepEqual(await modelEndpoint.probe(settings, 'messages', { fetchImpl: reply(status) }), { ok: true, status });
});

test('the probe retries an unreachable server and then blames the network policy', async () => {
  let calls = 0;
  const refused = async () => { calls++; throw Object.assign(new TypeError('fetch failed'), { cause: { code: 'ECONNREFUSED' } }); };

  const result = await modelEndpoint.probe(modelEndpoint.settings(endpointEnv()), 'chat',
    { fetchImpl: refused, attempts: 3, delayMs: 1 });

  assert.equal(calls, 3);
  assert.match(result.message, /Cannot reach the model endpoint .*ECONNREFUSED/);
  assert.match(result.message, /agent\.modelEndpoints/);
});

test('the probe sends the key, the model and nothing the model would run on, over real HTTP', async () => {
  const seen = [];
  const server = http.createServer((req, res) => {
    let body = '';
    req.on('data', chunk => { body += chunk; });
    req.on('end', () => {
      seen.push({ url: req.url, auth: req.headers.authorization, apiKey: req.headers['x-api-key'], body });
      res.writeHead(req.url === '/v1/messages' ? 400 : 404).end('{}');
    });
  });
  await new Promise(resolve => server.listen(0, '127.0.0.1', resolve));
  const base = `http://127.0.0.1:${server.address().port}/v1`;
  try {
    const settings = modelEndpoint.settings(endpointEnv({ AGENTHUB_MODEL_ENDPOINT_URL: base }));
    assert.equal((await modelEndpoint.probe(settings, 'messages')).ok, true);
    assert.equal((await modelEndpoint.probe(settings, 'chat')).ok, false);
  } finally {
    server.close();
  }
  assert.deepEqual(seen[0], {
    url: '/v1/messages', auth: 'Bearer sk-synthetic-not-real', apiKey: 'sk-synthetic-not-real',
    body: '{"model":"qwen3-coder-30b"}'
  });
  assert.equal(seen[1].url, '/v1/chat/completions');
});

test('a failed probe exits non-zero and writes the termination message the session list shows', async () => {
  const dir = fs.mkdtempSync(path.join(os.tmpdir(), 'model-endpoint-'));
  const termination = path.join(dir, 'termination-log');
  // A port that was just free: connecting is refused at once. Not a fixed low port such as 9,
  // which fetch refuses as a "bad port" before any connection is tried.
  const closed = http.createServer();
  await new Promise(resolve => closed.listen(0, '127.0.0.1', resolve));
  const port = closed.address().port;
  await new Promise(resolve => closed.close(resolve));
  try {
    // The real CLI waits out its retries (~10 s).
    const script = path.join(__dirname, '..', '..', 'common', 'model-endpoint.js');
    const result = spawnSync(process.execPath, [script, 'probe', 'chat'], {
      env: { ...process.env, ...endpointEnv({ AGENTHUB_MODEL_ENDPOINT_URL: `http://127.0.0.1:${port}/v1` }), TERMINATION_MESSAGE_PATH: termination },
      encoding: 'utf8'
    });
    assert.equal(result.status, 1, result.stderr);
    assert.match(fs.readFileSync(termination, 'utf8'), new RegExp(`Cannot reach the model endpoint http://127\\.0\\.0\\.1:${port}/v1/chat/completions \\(ECONNREFUSED\\)`));
  } finally {
    fs.rmSync(dir, { recursive: true, force: true });
  }
});

test('the three entrypoints still parse', () => {
  for (const agent of ['claude', 'codex', 'opencode']) {
    const file = path.join(__dirname, '..', '..', agent, 'entrypoint.sh');
    const result = spawnSync('bash', ['-n', file], { encoding: 'utf8' });
    assert.equal(result.status, 0, agent + ': ' + result.stderr);
  }
});
