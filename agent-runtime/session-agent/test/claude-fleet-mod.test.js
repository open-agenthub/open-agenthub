'use strict';

// The agenthub-fleet mod's pure logic (agent-runtime/claude/mods/agenthub-fleet/hooks/lib.mjs).
// The hooks module itself runs only under `claude plugin test`, which the Claude image build
// executes; this covers the parts that need no mods API.
const test = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');

const modDir = path.join(__dirname, '..', '..', 'claude', 'mods', 'agenthub-fleet');
const lib = () => import('../../claude/mods/agenthub-fleet/hooks/lib.mjs');

test('the mod is a plugin: manifest, hooks module pointer, tests', () => {
  const manifest = JSON.parse(fs.readFileSync(path.join(modDir, '.claude-plugin', 'plugin.json'), 'utf8'));
  assert.equal(manifest.name, 'agenthub-fleet');
  const hooks = JSON.parse(fs.readFileSync(path.join(modDir, 'hooks', 'hooks.json'), 'utf8'));
  assert.deepEqual(hooks.modules, ['./register.js']);
  assert.ok(fs.existsSync(path.join(modDir, 'tests', 'agenthub-fleet.test.ts')));
});

test('the hooks module keeps to what static analysis can read', () => {
  const source = fs.readFileSync(path.join(modDir, 'hooks', 'register.js'), 'utf8');
  // Every mods API call is written in full; nothing is pulled off $ into a variable.
  assert.doesNotMatch(source, /const \{[^}]*\} = \$/);
  assert.doesNotMatch(source, /= \$\.\w+\s*$/m);
  // No timer globals or Node APIs: the hooks module has none, and the mod would fail to load.
  assert.doesNotMatch(source, /\bsetTimeout\b|\bsetInterval\b|\brequire\(|from 'node:|process\.env/);
  // Only relative imports from the plugin's own directory.
  for (const match of source.matchAll(/from '([^']+)'/g)) assert.match(match[1], /^\.\//);
  // Event names are string literals.
  for (const match of source.matchAll(/on\(([^,)]+)/g)) assert.match(match[1], /^'[a-z.]+'$/);
});

test('messages are headed like the terminal channel heads them', async () => {
  const { formatFleetMessage } = await lib();
  assert.equal(formatFleetMessage({ from: 'rev-1', fromTitle: 'Reviewer', body: 'look' }),
    '[AgentHub message from agent "Reviewer" (rev-1)]\nlook');
  assert.equal(formatFleetMessage({ from: null, body: 'look' }), '[AgentHub message from outside the fleet]\nlook');
});

test('the inbox answer is parsed defensively', async () => {
  const { parseInbox } = await lib();
  assert.deepEqual(parseInbox('not json'), []);
  assert.deepEqual(parseInbox('{"messages":"nope"}'), []);
  assert.deepEqual(parseInbox(JSON.stringify({ messages: [
    { id: 'm-1', from: 'a', fromTitle: 'A', body: 'x', priority: true, interrupt: 'yes' },
    { body: '   ' },
    null,
    { body: 'plain' }
  ] })), [
    { id: 'm-1', from: 'a', fromTitle: 'A', body: 'x', priority: true, interrupt: false },
    { id: '', from: null, fromTitle: null, body: 'plain', priority: false, interrupt: false }
  ]);
});

test('a poll is planned: priority submits, interrupt aborts only during a turn, the rest waits', async () => {
  const { planDelivery } = await lib();
  const urgent = { body: 'a', priority: true, interrupt: false };
  const stop = { body: 'b', priority: true, interrupt: true };
  const plain = { body: 'c', priority: false, interrupt: false };

  assert.deepEqual(planDelivery([urgent, plain], true), { submit: [urgent], waiting: [plain], abort: false });
  assert.deepEqual(planDelivery([stop], true), { submit: [stop], waiting: [], abort: true });
  assert.deepEqual(planDelivery([stop], false), { submit: [stop], waiting: [], abort: false });
  assert.deepEqual(planDelivery([], true), { submit: [], waiting: [], abort: false });
});

test('status line and /inbox text', async () => {
  const { inboxStatus, inboxText } = await lib();
  assert.equal(inboxStatus(1), '📨 1 fleet message — /inbox');
  assert.equal(inboxStatus(3), '📨 3 fleet messages — /inbox');
  assert.equal(inboxText([]), 'No fleet messages are waiting.');
  assert.equal(inboxText([{ from: 'a', body: 'x' }, { from: null, body: 'y' }]),
    '[AgentHub message from agent "a" (a)]\nx\n\n[AgentHub message from outside the fleet]\ny');
});
