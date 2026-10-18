'use strict';

const assert = require('node:assert/strict');
const test = require('node:test');
const fs = require('node:fs');
const os = require('node:os');
const path = require('node:path');
const { createHarness, createChatHarness, requestHttp } = require('./common-server-harness');

const AUTH = { 'X-Agent-Token': 'correct-token', 'Content-Type': 'application/json' };
const ID = '0123456789abcdef0123456789abcdef';
const RECORD = Object.freeze({ id: ID, name: 'pasted-image-20261018-161200.png', mimeType: 'image/png', size: 142,
  localPath: '/workspace/.agenthub/files/' + ID + '/pasted-image-20261018-161200.png' });

function post(harness, body, headers = AUTH) {
  return requestHttp(harness, 'POST', '/agenthub/attachments', headers, typeof body === 'string' ? body : JSON.stringify(body));
}

test('materializes the files and answers with the path each is pasted as', async () => {
  const harness = createHarness({ AGENTHUB_CALLBACK_TOKEN: 'correct-token' });
  harness.materializer.materializeResult = [RECORD];
  const response = await post(harness, { fileIds: [ID] });
  assert.equal(response.status, 200);
  assert.equal(response.headers['cache-control'], 'no-store');
  assert.deepEqual(harness.materializer.calls, [[ID]]);
  assert.deepEqual(JSON.parse(response.body).attachments, [{ id: ID, name: RECORD.name, mimeType: 'image/png', size: 142, path: RECORD.localPath }]);
  // Nothing is typed by the pod: the browser pastes the paths ahead of its own text.
  assert.deepEqual(harness.terminals[0].writes, []);
});

test('authenticates, takes POST only and refuses a malformed body', async () => {
  const harness = createHarness({ AGENTHUB_CALLBACK_TOKEN: 'correct-token' });
  assert.equal((await post(harness, { fileIds: [ID] }, {})).status, 401);
  assert.equal((await requestHttp(harness, 'GET', '/agenthub/attachments', AUTH)).status, 405);
  assert.equal((await post(harness, 'not json')).status, 400);
  assert.equal((await post(harness, 'x'.repeat(5000))).status, 413);
  assert.deepEqual(harness.materializer.calls, []);
});

test('an invalid batch is 400 and a materializer failure 502', async () => {
  const harness = createHarness({ AGENTHUB_CALLBACK_TOKEN: 'correct-token' });
  harness.materializer.materialize = async () => { throw new Error('attachment_batch_invalid'); };
  assert.equal((await post(harness, { fileIds: ['nope'] })).status, 400);
  harness.materializer.materialize = async () => { throw new Error('attachment_download_failed'); };
  const failed = await post(harness, { fileIds: [ID] });
  assert.equal(failed.status, 502);
  assert.equal(JSON.parse(failed.body).error, 'attachment_delivery_failed');
});

test('a path a CLI could not take as one paste is refused rather than half-recognised', async () => {
  const harness = createHarness({ AGENTHUB_CALLBACK_TOKEN: 'correct-token' });
  harness.materializer.materializeResult = [{ ...RECORD, name: 'my shot.png', localPath: '/workspace/.agenthub/files/' + ID + '/my shot.png' }];
  const response = await post(harness, { fileIds: [ID] });
  assert.equal(response.status, 422);
  assert.equal(JSON.parse(response.body).error, 'attachment_path_unsafe');
});

test('a driver hook decides the pasted path', async () => {
  const seen = [];
  const harness = createHarness({ AGENTHUB_CALLBACK_TOKEN: 'correct-token', HOME: '/home/agent' }, {
    terminalAttachmentPath: (file, context) => { seen.push([file.id, context.home]); return '/home/agent/.cli/media/' + file.name; }
  });
  harness.materializer.materializeResult = [RECORD];
  const response = await post(harness, { fileIds: [ID] });
  assert.equal(JSON.parse(response.body).attachments[0].path, '/home/agent/.cli/media/' + RECORD.name);
  assert.deepEqual(seen, [[ID, '/home/agent']]);
});

test('a chat-transport session refuses: its attachments travel with the message', async () => {
  const harness = createChatHarness({ AGENTHUB_CALLBACK_TOKEN: 'correct-token' });
  const response = await post(harness, { fileIds: [ID] });
  assert.equal(response.status, 409);
  assert.equal(JSON.parse(response.body).error, 'chat_transport');
});

test('OpenClaw copies the file under its own media directory, which it reads images from', t => {
  const driver = require('../../openclaw/driver.js');
  const root = fs.mkdtempSync(path.join(os.tmpdir(), 'openclaw-attach-'));
  t.after(() => fs.rmSync(root, { recursive: true, force: true }));
  const source = path.join(root, 'files', ID, 'shot.png');
  fs.mkdirSync(path.dirname(source), { recursive: true });
  fs.writeFileSync(source, 'png-bytes');
  const home = path.join(root, 'home');
  const target = driver.terminalAttachmentPath({ id: ID, name: 'shot.png', localPath: source }, { env: {}, home, fs });
  assert.equal(target, path.join(home, '.openclaw', 'media', 'agenthub', ID, 'shot.png'));
  assert.equal(fs.readFileSync(target, 'utf8'), 'png-bytes');
  assert.ok(!fs.lstatSync(target).isSymbolicLink());
  const custom = driver.terminalAttachmentPath({ id: ID, name: 'shot.png', localPath: source }, { env: { OPENCLAW_STATE_DIR: path.join(root, 'state') }, home, fs });
  assert.equal(custom, path.join(root, 'state', 'media', 'agenthub', ID, 'shot.png'));
});
