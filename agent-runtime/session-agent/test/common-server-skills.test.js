'use strict';

const assert = require('node:assert/strict');
const test = require('node:test');

const { createHarness, tick } = require('./common-server-harness');

const CALLBACK = {
  AGENTHUB_CALLBACK_URL: 'http://hub.invalid/internal/sessions/s1',
  AGENTHUB_CALLBACK_TOKEN: 'secret'
};

function skillsInterval(harness) {
  return harness.intervals.find(interval => interval.ms !== 30_000);
}

test('the session agent pulls the skill library every minute by default', async () => {
  const pulls = [];
  const harness = createHarness(CALLBACK, {}, {
    pullSkills: async (env, deps) => { pulls.push({ env, deps }); return { notModified: true }; }
  });
  const interval = skillsInterval(harness);
  assert.equal(interval.ms, 60_000);

  interval.callback();
  await tick();
  assert.equal(pulls.length, 1);
  assert.equal(pulls[0].env.AGENTHUB_CALLBACK_TOKEN, 'secret');
  assert.equal(typeof pulls[0].deps.fetch, 'function');
});

test('a pull still running when the next tick comes is not doubled', async () => {
  let release;
  let calls = 0;
  const harness = createHarness(CALLBACK, {}, {
    pullSkills: () => { calls++; return new Promise(resolve => { release = resolve; }); }
  });
  const interval = skillsInterval(harness);
  interval.callback();
  interval.callback();
  await tick();
  assert.equal(calls, 1);

  release({ notModified: true });
  await tick();
  await tick();
  interval.callback();
  await tick();
  assert.equal(calls, 2);
});

test('a failing pull is logged once per distinct error and never throws', async () => {
  const logged = [];
  const original = console.log;
  console.log = (line) => { if (String(line).includes('Skills')) logged.push(line); };
  try {
    const harness = createHarness(CALLBACK, {}, {
      pullSkills: async () => { throw new Error('skills request failed with 503'); }
    });
    const interval = skillsInterval(harness);
    for (let i = 0; i < 3; i++) {
      interval.callback();
      await tick();
      await tick();
    }
  } finally {
    console.log = original;
  }
  assert.deepEqual(logged, ['[agent] Skills: pull failed (skills request failed with 503)']);
});

test('the live pull can be turned off and needs the callback', () => {
  const off = createHarness({ ...CALLBACK, AGENTHUB_SKILLS_SYNC_SECONDS: '0' });
  assert.equal(skillsInterval(off), undefined);
  const noCallback = createHarness({});
  assert.equal(skillsInterval(noCallback), undefined);
  const custom = createHarness({ ...CALLBACK, AGENTHUB_SKILLS_SYNC_SECONDS: '15' });
  assert.equal(skillsInterval(custom).ms, 15_000);
});
