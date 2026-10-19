'use strict';

const assert = require('node:assert/strict');
const path = require('node:path');
const test = require('node:test');
const { pathToFileURL } = require('node:url');

const load = () => import(pathToFileURL(path.join(__dirname, '..', '..', 'opencode', 'turn-notify.mjs')).href);

// The plugin client's answers, shaped as 1.18.35 returned them to a probe plugin.
function client({ parentID, messages = [] } = {}) {
  return {
    session: {
      get: async () => ({ data: { id: 'ses_1', parentID } }),
      messages: async () => ({ data: messages })
    }
  };
}

const assistant = (text, extra = {}) => ({
  info: { role: 'assistant', time: { created: 1, completed: 2 }, ...extra },
  parts: [{ type: 'step-start' }, { type: 'text', text }, { type: 'step-finish', reason: 'stop' }]
});

async function notified(options, event = { type: 'session.idle', properties: { sessionID: 'ses_1' } }) {
  const { createTurnNotifier } = await load();
  const sent = [];
  const notify = createTurnNotifier({
    env: { AGENTHUB_MODE: 'interactive' }, ...options, sendImpl: async body => { sent.push(body); return true; }
  });
  await notify(event);
  return sent;
}

test('the end of an interactive turn relays the last assistant text as a question', async () => {
  const sent = await notified({ client: client({ messages: [
    { info: { role: 'user' }, parts: [{ type: 'text', text: 'go' }] },
    assistant('first'), assistant('Which branch should I use?')
  ] }) });
  assert.deepEqual(sent, [{ event: 'question', message: 'Which branch should I use?' }]);
});

test('a subagent finishing inside the turn is not relayed', async () => {
  assert.deepEqual(await notified({ client: client({ parentID: 'ses_parent', messages: [assistant('sub')] }) }), []);
});

test('an interrupted turn and unattended modes are not relayed', async () => {
  const aborted = assistant('partial', { error: { name: 'MessageAbortedError' } });
  assert.deepEqual(await notified({ client: client({ messages: [aborted] }) }), []);
  // What session.idle sees right after Escape on 1.18.35: stopped, not yet marked aborted (#198).
  const stopped = assistant('partial', { time: { created: 1 } });
  assert.deepEqual(await notified({ client: client({ messages: [stopped] }) }), []);
  // A failed turn still leaves the agent waiting and is relayed.
  const failed = assistant('', { time: { created: 1 }, error: { name: 'APIError' } });
  assert.equal((await notified({ client: client({ messages: [failed] }) })).length, 1);
  for (const mode of ['autonomous', 'scheduled']) {
    assert.deepEqual(await notified({ env: { AGENTHUB_MODE: mode }, client: client({ messages: [assistant('x')] }) }), []);
  }
});

test('other events are ignored and a failing client still sends the generic question', async () => {
  assert.deepEqual(await notified({ client: client() }, { type: 'session.status', properties: { sessionID: 'ses_1' } }), []);
  const broken = { session: { get: async () => { throw new Error('down'); }, messages: async () => ({}) } };
  const sent = await notified({ client: broken });
  assert.equal(sent.length, 1);
  assert.equal(sent[0].event, 'question');
  assert.match(sent[0].message, /waiting for your reply/);
});
