'use strict';
const assert = require('node:assert/strict');
const fs = require('node:fs');
const os = require('node:os');
const path = require('node:path');
const test = require('node:test');
const { disableUpdates } = require('../../openclaw/update-policy');

test('OpenClaw replaces restored updater opt-ins and preserves unrelated configuration', t => {
  const dir = fs.mkdtempSync(path.join(os.tmpdir(), 'agenthub-updates-'));
  t.after(() => fs.rmSync(dir, { recursive: true, force: true }));
  const file = path.join(dir, 'openclaw.json');
  const config = { agents: { defaults: { model: 'openai/test' } },
    update: { channel: 'beta', checkOnStart: true, auto: { enabled: true, stableDelayHours: 12 } } };
  fs.writeFileSync(file, JSON.stringify(config));
  disableUpdates(file);
  const actual = JSON.parse(fs.readFileSync(file, 'utf8'));
  assert.deepEqual(actual, { ...config, update: { ...config.update, checkOnStart: false,
    auto: { ...config.update.auto, enabled: false } } });
  disableUpdates(file);
  assert.deepEqual(JSON.parse(fs.readFileSync(file, 'utf8')), actual);
});
