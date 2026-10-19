'use strict';
const test = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const os = require('node:os');
const path = require('node:path');
const { failedTurnEnded, TAIL_BYTES } = require('../../codex/turn-activity');
const line = (type, at, error) => JSON.stringify({ type: 'event_msg', timestamp: new Date(at).toISOString(), payload: { type, error } }) + '\n';
test('real rollout failures expire at the next prompt and successful completions do not skip Stop', t => {
  const dir = fs.mkdtempSync(path.join(os.tmpdir(), 'turn-activity-'));t.after(() => fs.rmSync(dir, { recursive: true }));
  const file = path.join(dir, 'rollout.jsonl');
  fs.writeFileSync(file, line('task_started', 1000) + line('task_complete', 1100, { message: 'Synthetic failure' }));
  assert.equal(failedTurnEnded({ file, after: 1000, fs }), true);
  assert.equal(failedTurnEnded({ file, after: 1200, fs }), false);
  fs.appendFileSync(file, line('task_started', 1200));
  assert.equal(failedTurnEnded({ file, after: 1200, fs }), false);
  fs.appendFileSync(file, line('task_complete', 1300));
  assert.equal(failedTurnEnded({ file, after: 1200, fs }), false);
  fs.appendFileSync(file, line('turn_aborted', 1400));
  assert.equal(failedTurnEnded({ file, after: 1200, fs }), true);
});
test('tail reads are bounded, cached, and tolerate a partially written next event', t => {
  const dir = fs.mkdtempSync(path.join(os.tmpdir(), 'turn-activity-'));t.after(() => fs.rmSync(dir, { recursive: true }));
  const file = path.join(dir, 'rollout.jsonl');fs.writeFileSync(file, 'x'.repeat(TAIL_BYTES * 2) + '\n' + line('task_complete', 2000, { message: 'Synthetic failure' }));
  let reads = 0;const observed = { ...fs, readSync(fd, buffer, offset, length, position) { reads++;assert(length <= TAIL_BYTES);return fs.readSync(fd, buffer, offset, length, position); } };
  assert.equal(failedTurnEnded({ file, after: 1900, fs: observed }), true);
  assert.equal(failedTurnEnded({ file, after: 1900, fs: observed }), true);assert.equal(reads, 1);
  fs.appendFileSync(file, '{"type":"event_msg",');assert.equal(failedTurnEnded({ file, after: 2100, fs: observed }), false);
});
