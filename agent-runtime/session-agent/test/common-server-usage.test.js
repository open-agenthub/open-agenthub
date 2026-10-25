'use strict';

const assert = require('node:assert/strict');
const test = require('node:test');
const { tick, createHarness } = require('./common-server-harness');

const ENV = {
  AGENTHUB_CALLBACK_URL: 'https://backend.invalid/internal/sessions/s1',
  AGENTHUB_CALLBACK_TOKEN: 'synthetic-callback-token'
};
const TOTALS = { inputTokens: 400, outputTokens: 50, cacheReadTokens: 600, cacheCreationTokens: 0,
  reasoningTokens: 20, turns: 1, costUsd: null };

const usagePosts = harness => harness.requests.filter(request => request.url.endsWith('/usage'));

test('the persistence tick posts the CLI usage totals, and again only once they changed', async () => {
  let totals = TOTALS;
  const harness = createHarness(ENV, { readUsage: () => totals });

  harness.intervals[0].callback();
  await tick();
  const [post] = usagePosts(harness);
  assert.equal(post.url, 'https://backend.invalid/internal/sessions/s1/usage');
  assert.equal(post.options.method, 'POST');
  assert.equal(post.options.headers['X-Agent-Token'], 'synthetic-callback-token');
  assert.deepEqual(JSON.parse(post.options.body), TOTALS);

  harness.intervals[0].callback();
  await tick();
  assert.equal(usagePosts(harness).length, 1, 'an unchanged total is not sent again');

  totals = { ...TOTALS, inputTokens: 800, turns: 2 };
  harness.intervals[0].callback();
  await tick();
  assert.equal(usagePosts(harness).length, 2);
  assert.equal(JSON.parse(usagePosts(harness)[1].options.body).inputTokens, 800);
});

test('a usage report the hub did not accept is sent again on the next tick', async () => {
  let accept = false;
  const harness = createHarness(ENV, { readUsage: () => TOTALS }, {
    fetchResponse: url => url.endsWith('/usage') ? { ok: accept, text: async () => '' } : undefined
  });

  harness.intervals[0].callback();
  await tick();
  accept = true;
  harness.intervals[0].callback();
  await tick();
  harness.intervals[0].callback();
  await tick();
  assert.equal(usagePosts(harness).length, 2);
});

test('no usage report without a readUsage driver, without totals, or when reading throws', async () => {
  for (const overrides of [{}, { readUsage: () => null }, { readUsage: () => { throw new Error('bad file'); } }]) {
    const harness = createHarness(ENV, overrides);
    harness.intervals[0].callback();
    await tick();
    assert.equal(usagePosts(harness).length, 0);
  }
});
