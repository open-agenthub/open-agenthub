'use strict';
const assert = require('node:assert/strict');
const test = require('node:test');
const catalog = require('../../common/model-catalog');
const { validateDriver } = require('../../common/driver-contract');
const claude = require('../../claude/model-catalog');
const codex = require('../../codex/model-catalog');
const cursor = require('../../cursor/model-catalog');
const opencode = require('../../opencode/model-catalog');
const { createHarness, requestHttp } = require('./common-server-harness');

// A run() stand-in: answers each call from the queue and records what the probe asked for.
function fakeRun(...answers) {
  const calls = [];
  const run = async options => {
    calls.push(options);
    const answer = answers.length > 1 ? answers.shift() : answers[0];
    return { stdout: '', stderr: '', code: 0, timedOut: false, ...answer };
  };
  return { run, calls };
}

const CLAUDE_INIT = JSON.stringify({ type: 'control_response', response: { subtype: 'success', request_id: 'agenthub-models', response: {
  claude_code_version: '2.1.318',
  account: { email: 'person@example.com', organization: 'Example Team Org', subscriptionType: 'Team' },
  models: [
    { value: 'default', resolvedModel: 'model-large-5', displayName: 'Default (recommended)', description: 'Large · $4/$20',
      supportsEffort: true, supportedEffortLevels: ['low', 'high'], supportsFastMode: true, supportsAdaptiveThinking: true, secretField: 'x' },
    { value: 'large[1m]', displayName: 'Large 1M', supportsEffort: false }
  ] } } });

test('every runtime with a model source declares it in the driver contract', () => {
  for (const agent of ['claude', 'codex', 'cursor', 'opencode']) {
    const driver = require(`../../${agent}/driver.js`);
    validateDriver(driver);
    assert.equal(typeof driver.modelCatalog.probe, 'function', agent);
  }
  assert.equal(require('../../openclaw/driver.js').modelCatalog, undefined);
  assert.throws(() => validateDriver({ ...require('../../claude/driver.js'), modelCatalog: {} }), /modelCatalog/);
});

test('normalising keeps the fixed fields only and refuses ids a shell or TOML would misread', () => {
  const models = catalog.normaliseModels([
    { id: 'good-1', label: 'Good', email: 'person@example.com', capabilities: { reasoningEfforts: ['low', 'low', 'BAD EFFORT'], contextWindow: 1000, url: 'https://x' } },
    { id: 'has space' }, { id: '"; rm -rf /' }, { id: '' }, { id: 'good-1' }, null,
    { id: 'opencode-go/glm-5.3' }, { id: 'claude-fable-5-1[1m]' }
  ]);
  assert.deepEqual(models.map(m => m.id), ['good-1', 'opencode-go/glm-5.3', 'claude-fable-5-1[1m]']);
  assert.deepEqual(models[0], { id: 'good-1', label: 'Good', capabilities: { reasoningEfforts: ['low'], contextWindow: 1000 } });
  const many = catalog.normaliseModels(Array.from({ length: 500 }, (_, i) => ({ id: 'm' + i })));
  assert.equal(many.length, catalog.MAX_MODELS);
  assert.ok(catalog.normaliseModel({ id: 'x', description: 'y'.repeat(5000) }).description.length <= 300);
});

test('error texts lose credential values and token-shaped strings', () => {
  const env = { CODEX_API_KEY: 'sk-live-0123456789abcdef', AGENTHUB_MODEL_API_KEY: 'endpoint-key-material' };
  const text = 'refused sk-live-0123456789abcdef and endpoint-key-material, Authorization: Bearer abcdefghijklmnop, ' +
    'token eyJhbGciOiJub25lIn0.eyJzdWIiOiJ4In0.sig and crsr_0123456789abcdef';
  const out = catalog.redact(text, env, t => t.replace('refused', 'masked-by-session'));
  for (const secret of ['sk-live-0123456789abcdef', 'endpoint-key-material', 'abcdefghijklmnop', 'eyJhbGciOiJub25lIn0', 'crsr_0123456789abcdef'])
    assert.ok(!out.includes(secret), secret);
  assert.match(out, /^masked-by-session/);
  assert.match(out, /Bearer \[redacted\]/);
});

test('Claude: the initialize answer becomes the catalog without the account\'s identity', async () => {
  const { run, calls } = fakeRun({ stdout: '{"type":"system"}\n' + CLAUDE_INIT + '\n' });
  const result = await claude.probe({ env: { CLAUDE_CODE_PLUGIN_DIRS: '/opt/mods', AGENTHUB_AUTH_MODE: 'subscription', X: '1' }, run });
  assert.equal(result.verified, true);
  assert.equal(result.cliVersion, '2.1.318');
  const models = catalog.normaliseModels(result.models);
  assert.deepEqual(models[0], { id: 'default', label: 'Default (recommended)', description: 'Large · $4/$20', resolvedId: 'model-large-5', isDefault: true,
    capabilities: { reasoningEfforts: ['low', 'high'], fastMode: true, adaptiveThinking: true } });
  assert.equal(models[1].id, 'large[1m]');
  const text = JSON.stringify({ ...result, models });
  for (const leak of ['person@example.com', 'Example Team Org', 'Team', 'secretField']) assert.ok(!text.includes(leak), leak);
  // A second agent must not start: no settings (hooks), no plugins (the fleet mod), no MCP, no transcript.
  const call = calls[0];
  assert.equal(call.cmd, 'claude');
  assert.deepEqual(call.args.slice(call.args.indexOf('--setting-sources'), call.args.indexOf('--setting-sources') + 2), ['--setting-sources', '']);
  assert.ok(call.args.includes('--no-session-persistence') && call.args.includes('--strict-mcp-config'));
  assert.equal(call.env.CLAUDE_CODE_PLUGIN_DIRS, undefined);
  assert.equal(call.env.X, '1');
  assert.match(call.input, /"subtype":"initialize"/);
  assert.equal(call.onStdout(CLAUDE_INIT + '\n'), true);
});

test('Claude: an API key list is reported as not verified, and no answer is an error', async () => {
  const init = CLAUDE_INIT.replace(/"account":\{[^}]*\}/, '"account":{"apiKeySource":"ANTHROPIC_API_KEY"}');
  assert.equal((await claude.probe({ env: {}, run: fakeRun({ stdout: init }).run })).verified, false);
  const failed = await claude.probe({ env: {}, run: fakeRun({ stdout: '', stderr: 'boom', code: 1 }).run });
  assert.equal(failed.state, 'error');
  const slow = await claude.probe({ env: {}, run: fakeRun({ timedOut: true }).run });
  assert.equal(slow.error.code, 'timeout');
});

function codexAnswer(data, nextCursor = null) {
  return JSON.stringify({ id: 1, result: { userAgent: 'agenthub-models/0.167.0 (Linux)' } }) + '\n' +
    JSON.stringify({ method: 'configWarning', params: {} }) + '\n' +
    JSON.stringify({ id: 2, result: { data, nextCursor } }) + '\n';
}
const CODEX_MODEL = { id: 'm-1', displayName: 'M 1', description: 'd', isDefault: true, hidden: false,
  supportedReasoningEfforts: [{ reasoningEffort: 'low' }, { reasoningEffort: 'high' }], defaultReasoningEffort: 'low',
  inputModalities: ['text', 'image'], additionalSpeedTiers: ['fast'] };

test('Codex: model/list rows, pages and capabilities', async () => {
  const { run, calls } = fakeRun({ stdout: codexAnswer([CODEX_MODEL, { ...CODEX_MODEL, id: 'hidden', hidden: true }], 'c2') },
    { stdout: codexAnswer([{ id: 'm-2' }]) });
  const result = await codex.probe({ env: { AGENTHUB_AUTH_MODE: 'subscription' }, run });
  assert.equal(result.state, 'ok');
  assert.equal(result.verified, true);
  assert.equal(result.cliVersion, '0.167.0');
  assert.deepEqual(catalog.normaliseModels(result.models), [
    { id: 'm-1', label: 'M 1', description: 'd', isDefault: true,
      capabilities: { reasoningEfforts: ['low', 'high'], defaultEffort: 'low', imageInput: true, fastMode: true } },
    { id: 'm-2' }]);
  assert.deepEqual(calls.map(c => c.args), [['app-server'], ['app-server']]);
  assert.match(calls[1].input, /"cursor":"c2"/);
});

test('Codex: a refused account list is a fallback, not the account\'s list', async () => {
  const stderr = '\x1b[31mERROR\x1b[0m codex_models_manager::manager: failed to refresh available models: unexpected status 403 Forbidden: not entitled\n';
  const refused = await codex.probe({ env: { AGENTHUB_AUTH_MODE: 'subscription' }, run: fakeRun({ stdout: codexAnswer([CODEX_MODEL]), stderr }).run });
  assert.equal(refused.state, 'fallback');
  assert.equal(refused.verified, false);
  assert.equal(refused.error.code, 'not_entitled');
  assert.equal(refused.models.length, 1);
  const key = await codex.probe({ env: { AGENTHUB_AUTH_MODE: 'apikey' }, run: fakeRun({ stdout: codexAnswer([CODEX_MODEL]) }).run });
  assert.deepEqual([key.state, key.verified], ['ok', false]);
});

test('Cursor: the account list as the CLI prints it, and its login errors', async () => {
  const printed = '\x1b[2mAvailable models\x1b[22m\n\n\x1b[36mauto\x1b[39m \x1b[2m- Auto\x1b[22m\x1b[2m (default)\x1b[22m\n' +
    'model-fast - Model Fast\nmodel-x[effort=high] - Model X (current)\n\nTip: use --model <id> to switch.\n';
  const ok = await cursor.probe({ env: {}, run: fakeRun({ stdout: printed }).run });
  assert.deepEqual([ok.state, ok.verified], ['ok', true]);
  assert.deepEqual(ok.models, [{ id: 'auto', label: 'Auto', isDefault: true }, { id: 'model-fast', label: 'Model Fast', isDefault: false },
    { id: 'model-x[effort=high]', label: 'Model X', isDefault: false }]);
  const login = await cursor.probe({ env: {}, run: fakeRun({ code: 1, stdout: 'Error: Authentication required. Run \'agent login\'' }).run });
  assert.equal(login.error.code, 'not_signed_in');
  const none = await cursor.probe({ env: {}, run: fakeRun({ stdout: 'No models available for this account.' }).run });
  assert.equal(none.error.code, 'not_entitled');
  assert.equal(cursor.parseModels('Available models\n\nsomething odd here!\n'), null);
});

test('OpenCode: the verbose catalogue without provider URLs, headers or options', async () => {
  const block = (id, info) => id + '\n' + JSON.stringify(info, null, 2) + '\n';
  const stdout = block('custom/m-1', { id: 'm-1', name: 'M One', api: { url: 'https://llm.example.com/v1' }, headers: { authorization: 'Bearer leaked-value-123' },
    options: { apiKey: 'leaked-key-value' }, limit: { context: 128000 }, capabilities: { reasoning: true, toolcall: true, input: { image: false } },
    variants: { low: {}, high: {} } }) + block('opencode/free', { name: 'Free' });
  const result = await opencode.probe({ run: fakeRun({ stdout }).run });
  assert.deepEqual([result.state, result.verified], ['ok', false]);
  const models = catalog.normaliseModels(result.models);
  assert.deepEqual(models, [
    { id: 'custom/m-1', label: 'M One', capabilities: { reasoningEfforts: ['low', 'high'], imageInput: false, reasoning: true, toolCall: true, contextWindow: 128000 } },
    { id: 'opencode/free', label: 'Free' }]);
  assert.ok(!JSON.stringify(models).includes('leaked'));
});

test('custom endpoint: the server\'s own list, and its refusal with the key masked', async () => {
  const env = { AGENTHUB_AUTH_MODE: 'customendpoint', AGENTHUB_MODEL_ENDPOINT_URL: 'https://llm.example.com/v1/', AGENTHUB_MODEL_NAME: 'served-b',
    AGENTHUB_MODEL_API_KEY: 'endpoint-secret-0001' };
  const seen = [];
  const listing = await catalog.probeEndpoint({ env, fetch: async (url, init) => {
    seen.push({ url, init });
    return { ok: true, status: 200, text: async () => JSON.stringify({ data: [{ id: 'served-a' }, { id: 'served-b', context_length: 32768 }] }) };
  } });
  assert.equal(seen[0].url, 'https://llm.example.com/v1/models');
  assert.equal(seen[0].init.headers.authorization, 'Bearer endpoint-secret-0001');
  assert.deepEqual(catalog.normaliseModels(listing.models), [{ id: 'served-a' }, { id: 'served-b', isDefault: true, capabilities: { contextWindow: 32768 } }]);

  const service = catalog.createModelCatalog({ driver: { name: 'Codex', stateDir: '.codex', authFilename: 'auth.json' }, env, home: '/nonexistent',
    fetch: async () => ({ ok: false, status: 403, text: async () => 'key endpoint-secret-0001 has no access' }) });
  const refused = await service.get();
  assert.equal(refused.state, 'error');
  assert.equal(refused.error.code, 'not_entitled');
  assert.ok(!JSON.stringify(refused).includes('endpoint-secret-0001'));
  assert.equal(refused.source, 'endpoint');
});

test('the catalog is cached per credential: an account switch, a refresh or an error asks again', async () => {
  const files = { '/home/agent/.test/auth.json': 'account-a' };
  const fs = { readFileSync: file => { if (file in files) return Buffer.from(files[file]); throw new Error('ENOENT'); } };
  let probes = 0;
  let fail = false;
  let release;
  const driver = { name: 'Test', stateDir: '.test', authFilename: 'auth.json', modelCatalog: { probe: async () => {
    probes++;
    if (release) await new Promise(resolve => { const r = release; release = null; r(resolve); });
    return fail ? { state: 'error', error: { code: 'probe_failed', message: 'no' } } : { state: 'ok', models: [{ id: 'm-' + files['/home/agent/.test/auth.json'] }] };
  } } };
  let clock = 1_000;
  const service = catalog.createModelCatalog({ driver, env: { AGENTHUB_AUTH_MODE: 'subscription' }, home: '/home/agent', fs, now: () => clock });
  assert.equal((await service.get()).models[0].id, 'm-account-a');
  await service.get();
  assert.equal(probes, 1);
  files['/home/agent/.test/auth.json'] = 'account-b';
  assert.equal((await service.get()).models[0].id, 'm-account-b');
  assert.equal(probes, 2);
  await service.get({ refresh: true });
  assert.equal(probes, 3);
  clock += catalog.CACHE_MS + 1;
  await service.get();
  assert.equal(probes, 4);
  fail = true;
  clock += catalog.CACHE_MS + 1;
  assert.equal((await service.get()).state, 'error');
  assert.equal((await service.get()).state, 'error');
  assert.equal(probes, 6);
  // Concurrent requests share one probe process.
  fail = false;
  let gate;
  release = resolve => { gate = resolve; };
  const both = Promise.all([service.get({ refresh: true }), service.get({ refresh: true })]);
  await new Promise(resolve => setImmediate(resolve));
  gate();
  const [first, second] = await both;
  assert.equal(first, second);
  assert.equal(probes, 7);
});

test('GET /agenthub/models needs the hub token and is never cached', async () => {
  const harness = createHarness({ AGENTHUB_CALLBACK_TOKEN: 'correct-token' });
  assert.equal((await requestHttp(harness, 'GET', '/agenthub/models', {})).status, 401);
  const plain = await requestHttp(harness, 'GET', '/agenthub/models', { 'X-Agent-Token': 'correct-token' });
  assert.equal(plain.status, 200);
  assert.equal(plain.headers['cache-control'], 'no-store');
  assert.equal(JSON.parse(plain.body).state, 'unsupported');
  assert.equal((await requestHttp(harness, 'POST', '/agenthub/models', { 'X-Agent-Token': 'correct-token' })).status, 405);
});
