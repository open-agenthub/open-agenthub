'use strict';

const assert = require('node:assert/strict');
const fs = require('node:fs');
const os = require('node:os');
const path = require('node:path');
const test = require('node:test');

const refreshModule = import('../../opencode/skill-refresh.mjs');

function setup(generation = 1) {
  const home = fs.mkdtempSync(path.join(os.tmpdir(), 'agenthub-oc-refresh-'));
  fs.mkdirSync(path.join(home, '.claude', 'skills'), { recursive: true });
  const write = (value) => fs.writeFileSync(path.join(home, '.claude', 'skills', '.agenthub-managed.json'),
    JSON.stringify({ skills: [], hashes: {}, generation: value }));
  write(generation);
  const disposed = [];
  const client = { instance: { dispose: async () => { disposed.push(Date.now()); } } };
  const timers = [];
  return {
    home, write, disposed, client, timers,
    options: (extra = {}) => ({
      client,
      env: { HOME: home, AGENTHUB_MODE: 'interactive' },
      setIntervalImpl: (fn, ms) => { timers.push({ fn, ms }); return timers.length; },
      clearIntervalImpl: (id) => { timers[id - 1].cleared = true; },
      ...extra
    })
  };
}

const flush = () => new Promise(resolve => setImmediate(resolve));

test('disposes the instance once a pull changed the skills and nothing is running', async () => {
  const { createSkillRefresher } = await refreshModule;
  const s = setup();
  const refresher = createSkillRefresher(s.options());
  assert.equal(s.timers[0].ms, 5_000);

  assert.equal(refresher.check(), false);
  s.write(2);
  assert.equal(refresher.check(), true);
  await flush();
  assert.equal(s.disposed.length, 1);
  // The instance is gone; its timer goes with it, the next instance's plugin starts a new one.
  assert.equal(s.timers[0].cleared, true);
  assert.equal(refresher.check(), false);
});

test('waits for a busy session to finish its turn', async () => {
  const { createSkillRefresher } = await refreshModule;
  const s = setup();
  const refresher = createSkillRefresher(s.options());
  refresher.onEvent({ type: 'session.status', properties: { sessionID: 'ses1', status: { type: 'busy' } } });
  s.write(2);
  assert.equal(refresher.check(), false);
  await flush();
  assert.equal(s.disposed.length, 0);

  // A retry (rate limit) is still the same turn.
  refresher.onEvent({ type: 'session.status', properties: { sessionID: 'ses1', status: { type: 'retry' } } });
  assert.equal(s.disposed.length, 0);

  refresher.onEvent({ type: 'session.idle', properties: { sessionID: 'ses1' } });
  await flush();
  assert.equal(s.disposed.length, 1);
});

test('stays out of unattended runs and when the live pull is off', async () => {
  const { createSkillRefresher } = await refreshModule;
  const s = setup();
  const autonomous = createSkillRefresher(s.options({ env: { HOME: s.home, AGENTHUB_MODE: 'autonomous' } }));
  const off = createSkillRefresher(s.options({
    env: { HOME: s.home, AGENTHUB_MODE: 'interactive', AGENTHUB_SKILLS_SYNC_SECONDS: '0' } }));
  s.write(2);
  assert.equal(autonomous.check(), false);
  assert.equal(off.check(), false);
  assert.equal(s.timers.length, 0);
});

test('a client without dispose and a missing manifest are harmless', async () => {
  const { createSkillRefresher, readGeneration } = await refreshModule;
  const s = setup();
  const refresher = createSkillRefresher(s.options({ client: {} }));
  s.write(2);
  assert.equal(refresher.check(), false);
  assert.equal(readGeneration(path.join(s.home, 'missing.json')), 0);
});

test('a newer instance replaces the previous refresher', async () => {
  const { installSkillRefresher } = await refreshModule;
  const s = setup();
  const first = installSkillRefresher(s.options());
  const second = installSkillRefresher(s.options());
  assert.equal(s.timers[0].cleared, true);
  s.write(2);
  assert.equal(first.check(), false);
  assert.equal(second.check(), true);
  second.stop();
});
