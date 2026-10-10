'use strict';

// The usage-limit detector (common/limit-detector.js) and each driver's patterns, fed the
// sentences the pinned CLIs print (docs/account-limits.md has the table and where each string
// was read). The Claude sentences are the fallback for a session without the mod.
const test = require('node:test');
const assert = require('node:assert/strict');

const { createLimitDetector, matchLimit, stripAnsi, toIso } = require('../../common/limit-detector');
const { validateDriver } = require('../../common/driver-contract');
const claude = require('../../claude/driver');
const codex = require('../../codex/driver');
const cursor = require('../../cursor/driver');
const openclaw = require('../../openclaw/driver');

const NOW = Date.UTC(2026, 9, 11, 12, 0, 0);
const now = () => NOW;

function detector(patterns) {
  const hits = [];
  const instance = createLimitDetector({ patterns, now, onHit: hit => hits.push(hit) });
  return { instance, hits };
}

test('strips the control sequences a TUI wraps its text in', () => {
  assert.equal(stripAnsi('\x1b[31mYou\'ve hit\x1b[0m your limit\x1b[2K\x1b]0;title\x07 now'), 'You\'ve hit your limit now');
});

test('every driver passes the contract with its patterns, and the contract rejects malformed ones', () => {
  for (const driver of [claude, codex, cursor, openclaw]) {
    assert.doesNotThrow(() => validateDriver(driver), driver.name);
    assert.ok(driver.limitPatterns.length > 0, driver.name);
  }
  const valid = { ...cursor };
  assert.throws(() => validateDriver({ ...valid, limitPatterns: 'x' }), /limitPatterns must be an array/);
  assert.throws(() => validateDriver({ ...valid, limitPatterns: [{ pattern: 'not a regexp' }] }), /limitPatterns entries/);
  assert.throws(() => validateDriver({ ...valid, limitPatterns: [{ pattern: /x/, resetsAt: 'no' }] }), /limitPatterns entries/);
});

test('fires once per start, from the first feed that sees the notice, across chunk boundaries', () => {
  const { instance, hits } = detector(codex.limitPatterns);

  instance.feedTerminal('\x1b[1mYou\'ve hit your ');
  assert.deepEqual(hits, []);
  instance.feedTerminal('usage limit. Try again at 3:15 PM.\r\n');
  instance.feedTerminal('You\'ve hit your usage limit.\r\n');
  instance.feedStderr('Usage limit reached');
  assert.equal(hits.length, 1);
  assert.deepEqual(hits[0], { detail: 'You\'ve hit your usage limit. Try again at 3:15 PM.', resetsAt: null });
  assert.equal(instance.fired, true);

  instance.reset();
  assert.equal(instance.fired, false);
  instance.feedTerminal('Usage limit reached\n');
  assert.equal(hits.length, 2);
});

test('Codex: the TUI sentences and the exec --json codes, with the reset read from the JSON body', () => {
  const { instance, hits } = detector(codex.limitPatterns);
  // String table of @openai/codex 0.160.0: "You've hit your usage limit." and the usage_limit_reached code.
  instance.feedTerminal('{"type":"error","message":"usage_limit_reached: You\'ve hit your usage limit.","resets_at":1791000000,"plan_type":"plus"}\n');
  assert.equal(hits.length, 1);
  assert.equal(hits[0].resetsAt, new Date(1791000000 * 1000).toISOString());

  const seconds = detector(codex.limitPatterns);
  seconds.instance.feedTerminal('{"error":{"code":"rate_limit_reached","reset_after_seconds":900}}\n');
  assert.equal(seconds.hits[0].resetsAt, new Date(NOW + 900_000).toISOString());

  for (const line of [
    'Usage limit reached. You\'ve reached your usage limit. Increase your limits to continue using codex.',
    'workspace_member_usage_limit_reached',
    'quota_exceeded'
  ]) {
    const one = detector(codex.limitPatterns);
    one.instance.feedTerminal(line + '\n');
    assert.equal(one.hits.length, 1, line);
    assert.equal(one.hits[0].resetsAt, null, line);
  }
});

test('OpenClaw: the notices openclaw 2026.7.1-2 prints for its upstream at a limit', () => {
  for (const line of [
    'Your Codex usage limit is reached.',
    'weekly Codex usage limit is reached',
    'Anthropic returned a billing error — check your account for subscription or usage limits, then try again.',
    'API rate limit reached. Please try again later.',
    'The model provider returned HTTP 429 before replying. This can mean rate limiting, exhausted quota, or an account balance problem.'
  ]) {
    const { instance, hits } = detector(openclaw.limitPatterns);
    instance.feedTerminal(line + '\n');
    assert.equal(hits.length, 1, line);
    assert.equal(hits[0].detail, line);
  }
});

test('Claude fallback: the CLI sentences, an ISO reset only when one is in the line', () => {
  for (const line of ['You\'ve hit your limit', 'You\'ve hit your usage limit', 'You\'ve hit your weekly limit · resets at 3pm',
    'Usage limit reached · resets in 2h', 'You\'re out of extra usage']) {
    const { instance, hits } = detector(claude.limitPatterns);
    instance.feedTerminal('\x1b[33m' + line + '\x1b[0m\r\n');
    assert.equal(hits.length, 1, line);
    assert.equal(hits[0].resetsAt, null, line);
  }
  const iso = detector(claude.limitPatterns);
  iso.instance.feedTerminal('Usage limit reached · resets at 2026-10-11T15:00:00Z\n');
  assert.equal(iso.hits[0].resetsAt, '2026-10-11T15:00:00.000Z');
});

test('Cursor: conservative sentences only, never a bare "rate limit"', () => {
  const { instance, hits } = detector(cursor.limitPatterns);
  instance.feedTerminal('GitHub API rate limit exceeded (5,000/hr shared across all tools and agents)\n');
  instance.feedTerminal('Warning: rate limited — try again in a moment\n');
  assert.deepEqual(hits, []);
  instance.feedTerminal('You\'ve hit your usage limit\n');
  assert.equal(hits.length, 1);
});

test('a chat pipe: result and error events are searched, assistant text is not', () => {
  const { instance, hits } = detector(claude.limitPatterns);
  instance.feedChatLine('{"type":"assistant","message":{"content":[{"type":"text","text":"The docs say: You\'ve hit your usage limit"}]}}');
  instance.feedChatLine('not json');
  instance.feedChatLine('{"type":"result","subtype":"success","is_error":false,"result":"done"}');
  assert.deepEqual(hits, []);
  instance.feedChatLine('{"type":"result","subtype":"error_during_execution","is_error":true,"result":"You\'ve hit your usage limit · resets at 2026-10-11T15:00:00Z"}');
  assert.equal(hits.length, 1);
  assert.equal(hits[0].resetsAt, '2026-10-11T15:00:00.000Z');
  assert.match(hits[0].detail, /^You've hit your usage limit/);
});

test('reset times are normalised: unix seconds, milliseconds, ISO, junk', () => {
  assert.equal(toIso(1791000000), new Date(1791000000 * 1000).toISOString());
  assert.equal(toIso(1791000000000), new Date(1791000000000).toISOString());
  assert.equal(toIso('2026-10-11T15:00:00Z'), '2026-10-11T15:00:00.000Z');
  assert.equal(toIso('3:15 PM'), null);
  assert.equal(toIso(42), null);
  assert.equal(toIso(null), null);
  assert.equal(toIso({}), null);
});

test('a driver without patterns is a detector that never fires', () => {
  const { instance, hits } = detector(undefined);
  assert.equal(instance.armed, false);
  instance.feedTerminal('You\'ve hit your usage limit\n');
  assert.deepEqual(hits, []);
  assert.equal(matchLimit([], 'You\'ve hit your usage limit'), null);
});
