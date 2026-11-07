// The branch's probes against the other real CLIs (#461), each in a throwaway HOME:
//   Claude  — the SDK initialize list with an API key pointed at the mock; the session's hooks and
//             a plugin with a SessionStart hook must not run in the probe process.
//   Cursor  — no login and an invalid key end as `not_signed_in`, with the key masked.
//   OpenCode — the verbose catalogue with a custom provider whose options hold a key; the key and
//             the provider URL must not reach the result.
// Usage: node cli-probes.mjs <worktree> <bin dir with claude, opencode, agent>   (mock.mjs running)
import fs from 'node:fs';
import os from 'node:os';
import path from 'node:path';
import { createRequire } from 'node:module';

const [wt, binDir] = process.argv.slice(2);
const require = createRequire(import.meta.url);
const { createModelCatalog } = require(path.join(wt, 'agent-runtime/common/model-catalog.js'));
const work = fs.mkdtempSync(path.join(process.env.TMPDIR || os.tmpdir(), 'cli-probes-'));
const results = [];
const check = (name, ok, detail = '') => { results.push(ok); console.log(`${ok ? 'PASS' : 'FAIL'} ${name}${detail ? ' — ' + detail : ''}`); };

async function probe(agent, env) {
  const driver = require(path.join(wt, `agent-runtime/${agent}/driver.js`));
  const home = env.HOME;
  const catalog = createModelCatalog({ driver, env: { PATH: `${binDir}:${path.dirname(process.execPath)}:/usr/bin:/bin`, ...env }, home, cwd: home });
  return catalog.get();
}

// ---- Claude ----
{
  const home = path.join(work, 'claude');
  const marker = path.join(home, 'hook-ran.log');
  fs.mkdirSync(path.join(home, '.claude'), { recursive: true });
  fs.writeFileSync(path.join(home, '.claude/settings.json'), JSON.stringify({ hooks: {
    SessionStart: [{ hooks: [{ type: 'command', command: `echo settings-session-start >> ${marker}` }] }],
    SessionEnd: [{ hooks: [{ type: 'command', command: `echo settings-session-end >> ${marker}` }] }] } }));
  const plugin = path.join(home, 'mods/fleet-standin');
  fs.mkdirSync(path.join(plugin, '.claude-plugin'), { recursive: true });
  fs.mkdirSync(path.join(plugin, 'hooks'), { recursive: true });
  fs.writeFileSync(path.join(plugin, '.claude-plugin/plugin.json'), JSON.stringify({ name: 'fleet-standin', version: '0.0.1' }));
  fs.writeFileSync(path.join(plugin, 'hooks/hooks.json'), JSON.stringify({ hooks: {
    SessionStart: [{ hooks: [{ type: 'command', command: `echo plugin-session-start >> ${marker}` }] }] } }));
  const key = 'sk-test-AAAA1111-claude';
  const result = await probe('claude', { HOME: home, AGENTHUB_AUTH_MODE: 'apikey', ANTHROPIC_API_KEY: key,
    ANTHROPIC_BASE_URL: 'http://127.0.0.1:18181', CLAUDE_CODE_PLUGIN_DIRS: plugin, CLAUDE_CODE_VERSION: '2.1.318' });
  check('Claude lists its models from the real CLI', result.state === 'ok' && result.models.some(m => m.id === 'default'),
    `${result.state} ${result.cliVersion} ${result.models.map(m => m.id).join(',')}`);
  check('Claude with an API key is reported as not verified', result.verified === false);
  check('Claude models carry their controls', Boolean(result.models[0]?.capabilities?.reasoningEfforts?.length),
    JSON.stringify(result.models[0]?.capabilities));
  check('no settings hook and no plugin hook ran in the probe', !fs.existsSync(marker), fs.existsSync(marker) ? fs.readFileSync(marker, 'utf8').trim() : 'marker absent');
  check('no transcript was written by the probe', !fs.existsSync(path.join(home, '.claude/projects')));
  check('the key is not in the result', !JSON.stringify(result).includes(key));

  // Negative control: the same initialize without the probe's flags and environment does run both
  // hooks, so their absence above is the flags' doing.
  const { spawn } = await import('node:child_process');
  const plain = spawn('claude', ['-p', '--input-format', 'stream-json', '--output-format', 'stream-json', '--verbose'], {
    cwd: home, env: { PATH: `${binDir}:${path.dirname(process.execPath)}:/usr/bin:/bin`, HOME: home, ANTHROPIC_API_KEY: key,
      ANTHROPIC_BASE_URL: 'http://127.0.0.1:18181', CLAUDE_CODE_PLUGIN_DIRS: plugin } });
  plain.stdin.write(JSON.stringify({ type: 'control_request', request_id: 'x', request: { subtype: 'initialize' } }) + '\n');
  await new Promise(resolve => {
    let out = '';
    plain.stdout.on('data', d => { out += d; if (out.includes('control_response')) setTimeout(() => { plain.kill(); resolve(); }, 1500); });
    setTimeout(() => { plain.kill(); resolve(); }, 40000);
  });
  const ran = fs.existsSync(marker) ? fs.readFileSync(marker, 'utf8') : '';
  check('control: without the flags the settings and plugin hooks do run', ran.includes('settings-session-start') && ran.includes('plugin-session-start'),
    ran.trim().split('\n').join(', ') || 'nothing ran');
}

// ---- Cursor ----
{
  const home = path.join(work, 'cursor');
  fs.mkdirSync(home, { recursive: true });
  const none = await probe('cursor', { HOME: home, AGENTHUB_AUTH_MODE: 'subscription' });
  check('Cursor without a login is not_signed_in', none.state === 'error' && none.error?.code === 'not_signed_in', none.error?.message);
  const key = 'crsr_invalid_0123456789abcdef';
  const bad = await probe('cursor', { HOME: home, AGENTHUB_AUTH_MODE: 'apikey', CURSOR_API_KEY: key });
  check('Cursor with a refused key is not_signed_in', bad.state === 'error' && bad.error?.code === 'not_signed_in', bad.error?.message);
  check('the Cursor key is not in the result', !JSON.stringify(bad).includes(key));
}

// ---- OpenCode ----
{
  const home = path.join(work, 'opencode');
  const config = path.join(home, '.config/opencode');
  fs.mkdirSync(config, { recursive: true });
  const key = 'sk-test-opencode-provider-key-0001';
  fs.writeFileSync(path.join(config, 'opencode.json'), JSON.stringify({ provider: { proofprov: {
    npm: '@ai-sdk/openai-compatible', name: 'Proof provider',
    options: { baseURL: 'http://127.0.0.1:18181/v1', apiKey: key, headers: { 'x-proof-secret': key } },
    models: { 'proof-model': { name: 'Proof Model', limit: { context: 32768, output: 4096 } } } } } }));
  const result = await probe('opencode', { HOME: home, XDG_CONFIG_HOME: path.join(home, '.config'),
    XDG_DATA_HOME: path.join(home, '.local/share'), XDG_CACHE_HOME: path.join(home, '.cache'), AGENTHUB_AUTH_MODE: 'subscription' });
  const own = result.models.find(m => m.id === 'proofprov/proof-model');
  check('OpenCode lists the configured provider\'s model', Boolean(own), own ? JSON.stringify(own) : result.models.map(m => m.id).slice(0, 5).join(','));
  check('OpenCode catalogue is reported as not verified', result.verified === false);
  const text = JSON.stringify(result);
  check('the provider key, header and URL are not in the result', !text.includes(key) && !text.includes('127.0.0.1:18181'));
}

fs.rmSync(work, { recursive: true, force: true });
const failed = results.filter(ok => !ok).length;
console.log(`\n${results.length - failed}/${results.length} passed`);
process.exit(failed ? 1 : 0);
