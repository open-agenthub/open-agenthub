'use strict';

const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const test = require('node:test');

const selectModel = require('../../openclaw/select-model');

const ANTHROPIC_CATALOG = [
  'anthropic/claude-fable-5',
  'anthropic/claude-haiku-4-5',
  'anthropic/claude-opus-4-8',
  'anthropic/claude-sonnet-4-6',
  'anthropic/claude-sonnet-5'
].join('\n');

test('the provider follows whichever key the pod mounted', () => {
  assert.equal(selectModel.providerFor({ ANTHROPIC_API_KEY: 'k' }), 'anthropic');
  assert.equal(selectModel.providerFor({ OPENAI_API_KEY: 'k' }), 'openai');
  assert.equal(selectModel.providerFor({ CURSOR_API_KEY: 'k' }), 'cursor');
  assert.equal(selectModel.providerFor({}), null);
  // An empty value is not a mounted key; treating it as one would select a provider whose
  // credential is absent, which is the failure this whole module exists to prevent.
  assert.equal(selectModel.providerFor({ ANTHROPIC_API_KEY: '' }), null);
});

test('only the requested provider is considered', () => {
  const mixed = 'openai/gpt-5.5\nanthropic/claude-sonnet-5\n  anthropic/claude-opus-4-8  \n';
  assert.deepEqual(selectModel.parseCatalog(mixed, 'anthropic'),
    ['anthropic/claude-sonnet-5', 'anthropic/claude-opus-4-8']);
  assert.deepEqual(selectModel.parseCatalog(mixed, 'openai'), ['openai/gpt-5.5']);
  assert.deepEqual(selectModel.parseCatalog('', 'anthropic'), []);
});

test('preference order decides, not catalogue order', () => {
  // claude-fable-5 sorts first in the catalogue; taking the first entry would pick it.
  const catalog = selectModel.parseCatalog(ANTHROPIC_CATALOG, 'anthropic');
  assert.equal(selectModel.selectModel(catalog, 'anthropic'), 'anthropic/claude-sonnet-5');
});

test('a preference that the catalogue dropped falls through to the next one', () => {
  const withoutSonnet5 = selectModel.parseCatalog(
    'anthropic/claude-fable-5\nanthropic/claude-opus-4-8', 'anthropic');
  assert.equal(selectModel.selectModel(withoutSonnet5, 'anthropic'), 'anthropic/claude-opus-4-8');
});

test('an unrecognised catalogue still yields a model of the right provider', () => {
  // The point of not pinning: a future rename must leave the session usable rather than
  // failing because an id from this file no longer exists.
  const renamed = selectModel.parseCatalog('anthropic/claude-something-7', 'anthropic');
  assert.equal(selectModel.selectModel(renamed, 'anthropic'), 'anthropic/claude-something-7');
});

test('an explicit override wins over both', () => {
  const catalog = selectModel.parseCatalog(ANTHROPIC_CATALOG, 'anthropic');
  assert.equal(
    selectModel.selectModel(catalog, 'anthropic', 'anthropic/claude-haiku-4-5'),
    'anthropic/claude-haiku-4-5');
});

test('an empty catalogue selects nothing rather than guessing', () => {
  assert.equal(selectModel.selectModel([], 'cursor'), null);
});

test('main prints the model and reports a provider with no models', () => {
  const out = [];
  const err = [];
  const write = (sink) => (chunk) => { sink.push(chunk); return true; };
  const realOut = process.stdout.write;
  const realErr = process.stderr.write;
  process.stdout.write = write(out);
  process.stderr.write = write(err);
  let selected, empty, noKey;
  try {
    selected = selectModel.main(['anthropic'], {}, ANTHROPIC_CATALOG);
    // The Cursor case: the pinned build lists no cursor models at all, so a session keyed that
    // way can never reach one. Exiting here beats a TUI that comes up saying "no models
    // available" without naming a cause.
    empty = selectModel.main(['cursor'], {}, '');
    noKey = selectModel.main([], {}, ANTHROPIC_CATALOG);
  } finally {
    process.stdout.write = realOut;
    process.stderr.write = realErr;
  }

  assert.equal(selected, 0);
  assert.deepEqual(out, ['anthropic/claude-sonnet-5']);
  assert.equal(empty, 1);
  assert.equal(noKey, 1);
  assert.match(err.join(''), /no models for provider "cursor"/);
  assert.match(err.join(''), /no provider API key is present/);
});

test('the entrypoint selects the model before starting the agent', () => {
  const entrypoint = fs.readFileSync(
    path.join(__dirname, '..', '..', 'openclaw', 'entrypoint.sh'), 'utf8');
  // --all matters: without it the catalogue is limited to already-configured models, which on a
  // fresh state directory is only the OpenAI default this is meant to move away from.
  assert.match(entrypoint, /openclaw models list --all --plain --provider/);
  assert.match(entrypoint, /select-model\.js/);
  assert.match(entrypoint, /openclaw models set "\$OPENCLAW_MODEL"/);
  // Best-effort: paste-api-key validates the key's shape, and a rejection must not stop a
  // session whose key works through the environment.
  assert.match(entrypoint, /paste-api-key[^\n]*\|\| *$|paste-api-key[\s\S]{0,400}falling back/);
});
