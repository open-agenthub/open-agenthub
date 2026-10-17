'use strict';

const assert = require('node:assert/strict');
const path = require('node:path');
const test = require('node:test');
const { pathToFileURL } = require('node:url');

const load = () => import(pathToFileURL(path.join(__dirname, '..', '..', 'opencode', 'limit-notice.mjs')).href);

// session.status events as 1.18.35 delivered them to a probe plugin, mock model answering 429.
const GO = '5 hour usage limit reached. It will reset in 2 hours 15 minutes. To continue using this model now, ' +
  'enable usage from your available balance - https://opencode.ai/workspace/wrk_test/go';
const retry = (message, reason, attempt = 1) => ({
  type: 'session.status',
  properties: { sessionID: 'ses_1', status: { type: 'retry', attempt, message, action: reason ? { reason, provider: 'opencode-go' } : undefined, next: 1792139652474 } }
});

async function printed(env, events) {
  const { createLimitNotice } = await load();
  const out = [];
  const onEvent = createLimitNotice({ env, write: text => out.push(text) });
  for (const event of events) onEvent(event);
  return out;
}

test('an unattended run prints the Go and free-tier notices once, as OpenCode worded them', async () => {
  assert.deepEqual(await printed({ AGENTHUB_MODE: 'autonomous' }, [
    { type: 'session.status', properties: { sessionID: 'ses_1', status: { type: 'busy' } } },
    retry(GO, 'account_rate_limit'), retry(GO, 'account_rate_limit', 2)
  ]), ['\n[agenthub] ' + GO + '\n']);
  assert.deepEqual(await printed({ AGENTHUB_MODE: 'scheduled' }, [
    retry('Free usage exceeded, subscribe to Go', 'free_tier_limit')
  ]), ['\n[agenthub] Free usage exceeded, subscribe to Go\n']);
});

test('the interactive TUI shows the status itself, so nothing is written over it', async () => {
  assert.deepEqual(await printed({ AGENTHUB_MODE: 'interactive' }, [retry(GO, 'account_rate_limit')]), []);
  assert.deepEqual(await printed({}, [retry(GO, 'account_rate_limit')]), []);
});

test('a retry that is not an account limit stays quiet', async () => {
  assert.deepEqual(await printed({ AGENTHUB_MODE: 'autonomous' }, [
    retry('Provider is overloaded'), retry('Too Many Requests', 'other'), { type: 'session.idle', properties: {} }, null
  ]), []);
});
