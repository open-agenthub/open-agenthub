'use strict';

// OpenCode's own model list (#461): `opencode models --verbose`, the CLI's models database narrowed
// to the providers this session is logged in to. It is a catalogue, not an entitlement check: a
// model listed for a provider may still be refused by that account.
//
// Each entry is a "<provider>/<model>" line followed by a JSON object. The object also carries the
// provider's URL, headers and options — with a custom provider those can hold a key — so only the
// fields below are read from it.

const HEADER = /^([A-Za-z0-9][A-Za-z0-9._-]*\/[^\s{}]+)$/;

function parseVerbose(stdout) {
  const models = [];
  let id = null;
  let block = [];
  const flush = () => {
    if (!id) return;
    let info = {};
    try { info = JSON.parse(block.join('\n')); } catch {}
    models.push(toModel(id, info));
  };
  for (const line of stdout.replace(/\x1b\[[0-9;]*m/g, '').split('\n')) {
    const header = HEADER.exec(line.trim());
    if (header && !line.startsWith(' ')) {
      flush();
      id = header[1];
      block = [];
    } else if (id) {
      block.push(line);
    }
  }
  flush();
  return models;
}

function toModel(id, info) {
  const caps = info && typeof info.capabilities === 'object' && info.capabilities ? info.capabilities : {};
  const variants = info && info.variants && typeof info.variants === 'object' ? Object.keys(info.variants) : [];
  return {
    id,
    label: info && info.name,
    capabilities: {
      reasoning: typeof caps.reasoning === 'boolean' ? caps.reasoning : undefined,
      reasoningEfforts: variants.length ? variants : undefined,
      toolCall: typeof caps.toolcall === 'boolean' ? caps.toolcall : undefined,
      imageInput: caps.input && typeof caps.input.image === 'boolean' ? caps.input.image : undefined,
      contextWindow: info && info.limit && info.limit.context
    }
  };
}

async function probe({ run }) {
  const result = await run({ cmd: 'opencode', args: ['models', '--verbose'] });
  if (result.code !== 0 || result.timedOut) {
    return { source: 'cli', state: 'error', error: { code: result.timedOut ? 'timeout' : 'probe_failed',
      message: result.timedOut ? 'OpenCode did not answer in time.' : (result.stderr.trim().split('\n').pop() || 'OpenCode ended without a model list.') } };
  }
  return { source: 'cli', verified: false, state: 'ok', models: parseVerbose(result.stdout) };
}

module.exports = { parseVerbose, probe, toModel };
