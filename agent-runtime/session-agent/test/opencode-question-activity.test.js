'use strict';

// opencode/question-activity.mjs (#343): OpenCode's question dialog opening and closing, from the
// bus events 1.18.35 publishes (question.asked with the tool's questions, then question.replied
// or question.rejected), reported to the session agent like the Claude and Codex hooks do.
const assert = require('node:assert/strict');
const test = require('node:test');

function relay(env = { AGENTHUB_MODE: 'interactive' }) {
  const reports = [];
  const reportImpl = async (event, _env, _fetch, details = {}) => { reports.push({ event, ...details }); return null; };
  return import('../../opencode/question-activity.mjs').then(({ createQuestionRelay }) => ({ reports, relay: createQuestionRelay({ env, reportImpl }) }));
}

test('an asked question is reported with its questions, a reply or dismissal as answered', async () => {
  const { reports, relay: r } = await relay();
  await r.onEvent({ type: 'question.asked', properties: { id: 'que_1', sessionID: 'ses_1', questions: [
    { header: 'Failures', question: 'Which behaviour?', multiple: true, options: [{ label: 'Fail fast', description: 'At once' }] }] } });
  await r.onEvent({ type: 'question.replied', properties: { sessionID: 'ses_1', requestID: 'que_1', answers: [['Fail fast']] } });
  await r.onEvent({ type: 'question.asked', properties: { questions: 'bogus' } });
  await r.onEvent({ type: 'question.rejected', properties: { sessionID: 'ses_1', requestID: 'que_2' } });
  await r.onEvent({ type: 'session.idle', properties: { sessionID: 'ses_1' } });
  assert.deepEqual(reports, [
    { event: 'question', questions: [{ header: 'Failures', question: 'Which behaviour?', multiSelect: true, options: [{ label: 'Fail fast', description: 'At once' }] }] },
    { event: 'answered' }, { event: 'question' }, { event: 'answered' }
  ]);
});

test('an autonomous run reports nothing', async () => {
  const { reports, relay: r } = await relay({ AGENTHUB_MODE: 'autonomous' });
  await r.onEvent({ type: 'question.asked', properties: { questions: [{ question: 'Go?', options: [] }] } });
  assert.deepEqual(reports, []);
});
