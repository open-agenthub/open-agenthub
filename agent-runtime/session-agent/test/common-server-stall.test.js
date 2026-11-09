'use strict';
const assert = require('node:assert/strict');
const test = require('node:test');
const { createHarness, requestHttp, FakeSocket } = require('./common-server-harness');
const { createStallWatch, minutesSetting, stallSettings } = require('../../common/stall-watch');
const codexDriver = require('../../codex/driver');

// #562: a turn that stops progressing without ending. The screen keeps changing (Codex counts
// the turn's time every second), so only what the driver keeps of it can count as progress.
const headers = { 'X-Agent-Token': 'stall-token', 'Content-Type': 'application/json' };
const subscribe = socket => socket.emit('message', JSON.stringify({ type: 'subscribe', channel: 'activity' }));
const frames = socket => socket.sent.map(data => { try { return JSON.parse(data); } catch { return null; } }).filter(f => f?.subtype === 'activity');
const turn = (h, event) => requestHttp(h, 'POST', '/agenthub/turn', headers, JSON.stringify({ event }), '127.0.0.1');
const MIN = 60_000;

function rig(env = {}, mode = 'interactive') {
  const clock = { now: 1_000_000 };
  const h = createHarness({ AGENTHUB_CALLBACK_TOKEN: 'stall-token', AGENTHUB_MODE: mode, ...env },
    { turnHooks: true, progressScreen: codexDriver.progressScreen }, { now: () => clock.now });
  const socket = new FakeSocket(); h.runtime.webSocketServer.connect(socket, '/'); subscribe(socket);
  const pty = h.terminals[0];
  let seconds = 0;
  // The status row as Codex 0.167.0 draws it, its time counting up between checks.
  const statusRow = (text = 'Working') => `\x1b[3;1H\x1b[2K${seconds % 2 ? '◦' : '•'} ${text} (${seconds}s • esc to interrupt) · 1 background terminal running`;
  async function check(minutes, { content, status } = {}) {
    clock.now += minutes * MIN;
    seconds += minutes * 60;
    if (content) pty.emitData(`\x1b[2;1H\x1b[2K${content}`);
    pty.emitData(statusRow(status));
    h.intervals.find(i => i.ms === 15_000).callback();
    await new Promise(resolve => setTimeout(resolve, 30));
    return frames(socket).at(-1);
  }
  return { h, socket, pty, check };
}

test('a turn whose screen only counts its time is reported after the notice minutes', async () => {
  const { h, check } = rig();
  await turn(h, 'prompt');
  assert.equal((await check(0)).stalled, undefined);
  assert.equal((await check(19)).stalled, undefined);
  const frame = await check(2);
  assert.equal(frame.state, 'working');
  assert.deepEqual(frame.stalled, { minutes: 21, reason: 'no_progress' });
  // Interactive sessions are never interrupted by the runtime: the person decides.
  await check(120);
  assert.deepEqual(h.terminals[0].writes, []);
});

test('new content or a hook event is progress; the person typing is not', async () => {
  const { h, socket, check } = rig();
  await turn(h, 'prompt');
  await check(0);
  await check(15, { content: 'Ran npm run build' });
  assert.equal((await check(15)).stalled, undefined);
  assert.equal((await check(6)).stalled.minutes, 21);
  // A message typed into the stalled CLI redraws its queue panel: still stalled.
  socket.emit('message', JSON.stringify({ type: 'input', data: 'please answer' }));
  assert.equal((await check(1, { content: 'Messages to be submitted after next tool call' })).stalled.minutes, 22);
  // A hook says the turn moved on.
  await turn(h, 'permission');
  await turn(h, 'answered');
  assert.equal((await check(19)).stalled, undefined);
  // The turn ends: nothing to report, and the next turn starts its own clock.
  await turn(h, 'stop');
  assert.equal((await check(30)).stalled, undefined);
  await turn(h, 'prompt');
  assert.equal((await check(10)).stalled, undefined);
});

test('the reconnect status row is reported as its own reason', async () => {
  const { h, check } = rig();
  await turn(h, 'prompt');
  await check(0, { status: 'Reconnecting... waiting for network' });
  assert.deepEqual((await check(25, { status: 'Reconnecting... waiting for network' })).stalled, { minutes: 25, reason: 'reconnecting' });
});

test('a turn waiting for a person is not stalled', async () => {
  const { h, check } = rig();
  await turn(h, 'prompt');
  await turn(h, 'question');
  await check(0);
  const frame = await check(45);
  assert.equal(frame.state, 'waiting');
  assert.equal(frame.stalled, undefined);
});

test('an unattended run is interrupted once at the interrupt minutes, and says why', async () => {
  const { h, pty, socket, check } = rig({ AGENTHUB_STALLED_TURN_INTERRUPT_MINUTES: '60' }, 'autonomous');
  await check(0);
  await check(59);
  assert.deepEqual(pty.writes, []);
  await check(2);
  assert.deepEqual(pty.writes, ['\x03']);
  assert.ok(socket.sent.some(data => data.includes('[agent] Turn interrupted after 61 min without progress.')));
  await check(60);
  assert.deepEqual(pty.writes, ['\x03']);
});

test('the failed run says it was interrupted for want of progress', async () => {
  const { h, pty, check } = rig({ AGENTHUB_STALLED_TURN_INTERRUPT_MINUTES: '60', TERMINATION_MESSAGE_PATH: '/termination' }, 'autonomous');
  await check(0);
  await check(61);
  assert.deepEqual(pty.writes, ['\x03']);
  pty.emitExit({ exitCode: 1 });
  await new Promise(resolve => setTimeout(resolve, 30));
  const message = h.writes.find(write => write.file === '/termination');
  assert.match(message.data, /^Turn interrupted after 61 min without progress\. Test exited with code 1/);
});

test('a run that moves right before the limit is not interrupted, and one that ended gets no key', async () => {
  const { h, pty, check } = rig({ AGENTHUB_STALLED_TURN_INTERRUPT_MINUTES: '60' }, 'autonomous');
  await check(0);
  await check(59);
  // Its last output arrives in the same check that reaches the limit: that is progress.
  await check(1.5, { content: '{"type":"turn.completed"}' });
  assert.deepEqual(pty.writes, []);
  pty.emitExit({ exitCode: 0 });
  await check(120);
  assert.deepEqual(pty.writes, []);
});

test('0 turns the notice and the interrupt off', async () => {
  const { h, pty, check } = rig({ AGENTHUB_STALLED_TURN_NOTICE_MINUTES: '0', AGENTHUB_STALLED_TURN_INTERRUPT_MINUTES: '0' }, 'autonomous');
  await check(0);
  assert.equal((await check(600)).stalled, undefined);
  assert.deepEqual(pty.writes, []);
});

test('a driver without a progress view gets no stall watch', () => {
  const h = createHarness({ AGENTHUB_CALLBACK_TOKEN: 'stall-token' }, { turnHooks: true });
  assert.equal(h.intervals.some(i => i.ms === 15_000), false);
});

test('settings: unset is the default, 0 or junk is off, the ceiling is a day', () => {
  assert.deepEqual(stallSettings({}), { noticeMs: 20 * MIN, interruptMs: 60 * MIN });
  assert.equal(minutesSetting('0', 20), 0);
  assert.equal(minutesSetting('abc', 20), 0);
  assert.equal(minutesSetting('-5', 20), 0);
  assert.equal(minutesSetting('99999', 20), 24 * 60);
  const watch = createStallWatch({ now: () => 0, noticeMs: MIN, interruptMs: 0 });
  assert.equal(watch.stalled(), null);
  assert.equal(watch.takeInterrupt(), null);
});

test('the Codex view keeps the status header and drops only the spinner and the clock', () => {
  const view = rows => codexDriver.progressScreen(rows).key;
  assert.equal(view(['◦ Working (16s • esc to interrupt) · 1 background terminal running']),
    view(['• Working (17h 16m 37s • esc to interrupt) · 1 background terminal running']));
  assert.notEqual(view(['• Working (16s • esc to interrupt)']), view(['• Planning the fix (16s • esc to interrupt)']));
  // Cut to the terminal's width, the row ends somewhere else each time the time grows a digit.
  assert.equal(view(['◦ Working (59s • esc to interrupt) · 1 background terminal running · /ps to vie…']),
    view(['• Working (1m 00s • esc to interrupt) · 1 background terminal running · /ps to vi…']));
  assert.equal(view(['◦ Working (9m 59s • esc to inter…']), view(['• Working (10m 00s • esc to inte…']));
  assert.equal(view(['• Explored (2 files)']), ' Explored (2 files)');
  assert.equal(codexDriver.progressScreen(['◦ Reconnecting... waiting for network (29s • esc to interrupt)']).reconnecting, true);
  assert.equal(codexDriver.progressScreen(['• Working (29s • esc to interrupt)']).reconnecting, false);
});
