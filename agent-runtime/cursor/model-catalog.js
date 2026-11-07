'use strict';

// Cursor's own model list (#461): `agent models`, "List available models for this account". It
// needs a valid login and asks the service with it, so the answer is the account's. The CLI is not
// pinned; its text output ("<id> - <name> (current, default)" under "Available models") is parsed
// strictly, and anything else is an error rather than an empty list.

const LINE = /^([A-Za-z0-9][A-Za-z0-9._:/@[\]+=,-]*)(?:\s+-\s+(.*?))?(?:\s+\(([a-z, ]+)\))?$/;

function parseModels(stdout) {
  const text = stdout.replace(/\x1b\[[0-9;]*m/g, '');
  const lines = text.split('\n').map(line => line.trim());
  const start = lines.indexOf('Available models');
  if (start < 0) return null;
  const models = [];
  for (const line of lines.slice(start + 1)) {
    if (!line) continue;
    if (line.startsWith('Tip:')) break;
    const match = LINE.exec(line);
    if (!match) return null;
    const flags = (match[3] || '').split(',').map(flag => flag.trim());
    models.push({ id: match[1], label: match[2], isDefault: flags.includes('default') });
  }
  return models;
}

async function probe({ env, run }) {
  const result = await run({ cmd: 'agent', args: ['models'] });
  const out = (result.stdout + '\n' + result.stderr).replace(/\x1b\[[0-9;]*m/g, '');
  if (/No models available for this account/.test(out)) {
    return { source: 'cli', verified: true, state: 'error', error: { code: 'not_entitled', message: 'Cursor lists no models for this account.' } };
  }
  const models = result.code === 0 ? parseModels(result.stdout) : null;
  if (models) return { source: 'cli', verified: true, state: 'ok', models };
  const auth = /Authentication (required|failed)|API key is invalid/i.test(out);
  const message = out.split('\n').map(line => line.trim()).filter(Boolean).slice(0, 3).join(' ');
  return { source: 'cli', state: 'error', error: { code: result.timedOut ? 'timeout' : auth ? 'not_signed_in' : 'probe_failed',
    message: result.timedOut ? 'Cursor did not answer in time.' : message || 'Cursor ended without a model list.' } };
}

module.exports = { parseModels, probe };
