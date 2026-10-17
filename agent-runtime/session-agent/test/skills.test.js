'use strict';

const assert = require('node:assert/strict');
const fs = require('node:fs');
const os = require('node:os');
const path = require('node:path');
const test = require('node:test');

const { materializeSkills, pullSkills, linkAgentsSkills, MANIFEST } = require('../../common/skills');

function tempSkillsDir() {
  return path.join(fs.mkdtempSync(path.join(os.tmpdir(), 'agenthub-skills-')), 'skills');
}

function read(dir, name) {
  return fs.readFileSync(path.join(dir, name, 'SKILL.md'), 'utf8');
}

test('writes skills and a manifest', () => {
  const dir = tempSkillsDir();
  const result = materializeSkills(
    { skills: [{ name: 'review', content: '# review' }, { name: 'deploy', content: '# deploy' }] },
    dir);

  assert.deepEqual(result.written.sort(), ['deploy', 'review']);
  assert.equal(read(dir, 'review'), '# review');
  const manifest = JSON.parse(fs.readFileSync(path.join(dir, MANIFEST), 'utf8'));
  assert.deepEqual(manifest.skills.sort(), ['deploy', 'review']);
});

test('removes previously managed skills that disappeared from the library', () => {
  const dir = tempSkillsDir();
  materializeSkills({ skills: [{ name: 'old', content: '# old' }, { name: 'keep', content: '# keep' }] }, dir);
  const result = materializeSkills({ skills: [{ name: 'keep', content: '# keep v2' }] }, dir);

  assert.deepEqual(result.removed, ['old']);
  assert.equal(fs.existsSync(path.join(dir, 'old')), false);
  assert.equal(read(dir, 'keep'), '# keep v2');
});

test('never clobbers a skill directory the user created in the session', () => {
  const dir = tempSkillsDir();
  fs.mkdirSync(path.join(dir, 'mine'), { recursive: true });
  fs.writeFileSync(path.join(dir, 'mine', 'SKILL.md'), '# user made this');

  const result = materializeSkills({ skills: [{ name: 'mine', content: '# hub version' }] }, dir);

  assert.deepEqual(result.skipped, ['mine']);
  assert.equal(read(dir, 'mine'), '# user made this');
  // Unmanaged directories are never recorded as managed, so they also never get removed.
  const manifest = JSON.parse(fs.readFileSync(path.join(dir, MANIFEST), 'utf8'));
  assert.deepEqual(manifest.skills, []);
  const second = materializeSkills({ skills: [] }, dir);
  assert.deepEqual(second.removed, []);
  assert.equal(read(dir, 'mine'), '# user made this');
});

test('materializes extra files and rebuilds managed dirs so removed files disappear', () => {
  const dir = tempSkillsDir();
  materializeSkills({
    skills: [{
      name: 'deploy',
      content: '# deploy',
      files: [
        { path: 'scripts/check.sh', content: '#!/bin/sh\ntrue' },
        { path: 'notes.md', content: '# notes' },
        { path: '../escape.sh', content: 'nope' },
        { path: 'bad\0path', content: 'nope' }
      ]
    }]
  }, dir);

  assert.equal(fs.readFileSync(path.join(dir, 'deploy', 'scripts', 'check.sh'), 'utf8'), '#!/bin/sh\ntrue');
  assert.equal(fs.readFileSync(path.join(dir, 'deploy', 'notes.md'), 'utf8'), '# notes');
  assert.equal(fs.existsSync(path.join(dir, 'escape.sh')), false);

  // The library dropped notes.md — the managed dir is rebuilt without it.
  materializeSkills({
    skills: [{ name: 'deploy', content: '# deploy v2', files: [{ path: 'scripts/check.sh', content: 'false' }] }]
  }, dir);
  assert.equal(read(dir, 'deploy'), '# deploy v2');
  assert.equal(fs.existsSync(path.join(dir, 'deploy', 'notes.md')), false);
  assert.equal(fs.readFileSync(path.join(dir, 'deploy', 'scripts', 'check.sh'), 'utf8'), 'false');
});

test('skips invalid names and empty content', () => {
  const dir = tempSkillsDir();
  const result = materializeSkills({
    skills: [
      { name: '../escape', content: '# nope' },
      { name: 'UPPER', content: '# nope' },
      { name: 'empty', content: '' },
      { name: 'ok', content: '# fine' }
    ]
  }, dir);

  assert.deepEqual(result.written, ['ok']);
  assert.equal(result.skipped.length, 3);
  assert.equal(fs.existsSync(path.join(dir, '..', 'escape')), false);
});

test('handles a missing or malformed payload gracefully', () => {
  const dir = tempSkillsDir();
  assert.deepEqual(materializeSkills(null, dir).written, []);
  assert.deepEqual(materializeSkills({ skills: 'nope' }, dir).written, []);
});

test('keeps a managed skill that was edited locally instead of overwriting it', () => {
  const dir = tempSkillsDir();
  materializeSkills({ skills: [{ name: 'deploy', content: '# v1' }] }, dir);
  fs.writeFileSync(path.join(dir, 'deploy', 'SKILL.md'), '# v1 plus a local fix');

  const result = materializeSkills({ skills: [{ name: 'deploy', content: '# v2' }] }, dir);

  assert.deepEqual(result.kept, ['deploy']);
  assert.deepEqual(result.written, []);
  assert.equal(read(dir, 'deploy'), '# v1 plus a local fix');
  // Still managed, so the upward sync does not import it as a brand-new skill of the user's.
  const manifest = JSON.parse(fs.readFileSync(path.join(dir, MANIFEST), 'utf8'));
  assert.deepEqual(manifest.skills, ['deploy']);
});

test('keeps a locally edited skill even when the library removed it', () => {
  const dir = tempSkillsDir();
  materializeSkills({ skills: [{ name: 'deploy', content: '# v1' }] }, dir);
  fs.writeFileSync(path.join(dir, 'deploy', 'notes.md'), 'added in the session');

  const result = materializeSkills({ skills: [] }, dir);

  assert.deepEqual(result.kept, ['deploy']);
  assert.deepEqual(result.removed, []);
  assert.equal(read(dir, 'deploy'), '# v1');
});

test('takes a locally edited skill back once the library has the same content', () => {
  const dir = tempSkillsDir();
  materializeSkills({ skills: [{ name: 'deploy', content: '# v1' }] }, dir);
  fs.writeFileSync(path.join(dir, 'deploy', 'SKILL.md'), '# v2');
  // The agent uploaded its edit: the library now serves exactly the local content.
  assert.deepEqual(materializeSkills({ skills: [{ name: 'deploy', content: '# v2' }] }, dir).unchanged, ['deploy']);

  const result = materializeSkills({ skills: [{ name: 'deploy', content: '# v3' }] }, dir);
  assert.deepEqual(result.written, ['deploy']);
  assert.equal(read(dir, 'deploy'), '# v3');
});

test('leaves unchanged skills alone and bumps the generation only on a change', () => {
  const dir = tempSkillsDir();
  const payload = { skills: [{ name: 'deploy', content: '# v1', files: [{ path: 'a.sh', content: 'x' }] }] };
  assert.equal(materializeSkills(payload, dir).generation, 1);
  const before = fs.statSync(path.join(dir, 'deploy', 'SKILL.md')).mtimeMs;

  const again = materializeSkills(payload, dir);
  assert.deepEqual(again.unchanged, ['deploy']);
  assert.deepEqual(again.written, []);
  assert.equal(again.generation, 1);
  // Not rewritten: the agent CLIs that watch the directory see no event for a pull without news.
  assert.equal(fs.statSync(path.join(dir, 'deploy', 'SKILL.md')).mtimeMs, before);

  assert.equal(materializeSkills({ skills: [] }, dir).generation, 2);
});

test('stages outside the skills directory and leaves nothing behind', () => {
  const dir = tempSkillsDir();
  materializeSkills({ skills: [{ name: 'deploy', content: '# v1' }] }, dir);
  materializeSkills({ skills: [{ name: 'deploy', content: '# v2' }] }, dir);

  assert.deepEqual(fs.readdirSync(dir).sort(), [MANIFEST, 'deploy']);
  assert.deepEqual(fs.readdirSync(path.join(path.dirname(dir), '.agenthub-skills-staging')), []);
});

function fakeHub(responses) {
  const calls = [];
  const fetchImpl = async (url, options) => {
    calls.push({ url, headers: options.headers });
    const next = responses.shift();
    return {
      status: next.status,
      ok: next.status >= 200 && next.status < 300,
      headers: { get: (name) => (name.toLowerCase() === 'etag' ? next.etag || null : null) },
      json: async () => next.body
    };
  };
  return { calls, fetchImpl };
}

const pullEnv = (home) => ({ HOME: home, AGENTHUB_CALLBACK_URL: 'http://hub/internal/sessions/s1', AGENTHUB_CALLBACK_TOKEN: 'tok' });

test('pull sends the stored etag and does nothing on 304', async () => {
  const home = fs.mkdtempSync(path.join(os.tmpdir(), 'agenthub-pull-'));
  const hub = fakeHub([
    { status: 200, etag: '"e1"', body: { skills: [{ name: 'deploy', content: '# v1' }] } },
    { status: 304, etag: '"e1"' }
  ]);

  const first = await pullSkills(pullEnv(home), { fetch: hub.fetchImpl });
  assert.deepEqual(first.written, ['deploy']);
  const second = await pullSkills(pullEnv(home), { fetch: hub.fetchImpl });
  assert.equal(second.notModified, true);

  assert.equal(hub.calls[0].url, 'http://hub/internal/sessions/s1/skills');
  assert.equal(hub.calls[0].headers['If-None-Match'], undefined);
  assert.equal(hub.calls[1].headers['If-None-Match'], '"e1"');
  assert.equal(hub.calls[1].headers['X-Agent-Token'], 'tok');
  assert.equal(read(path.join(home, '.claude', 'skills'), 'deploy'), '# v1');
});

test('pull skips while another pull holds the lock, and takes over a stale one', async () => {
  const home = fs.mkdtempSync(path.join(os.tmpdir(), 'agenthub-pull-'));
  const lock = path.join(home, '.claude', '.agenthub-skills-sync.lock');
  fs.mkdirSync(lock, { recursive: true });
  const hub = fakeHub([{ status: 200, etag: '"e1"', body: { skills: [] } }]);

  assert.equal((await pullSkills(pullEnv(home), { fetch: hub.fetchImpl })).skippedReason, 'another pull is running');
  assert.equal(hub.calls.length, 0);

  const old = new Date(Date.now() - 10 * 60_000);
  fs.utimesSync(lock, old, old);
  const result = await pullSkills(pullEnv(home), { fetch: hub.fetchImpl });
  assert.deepEqual(result.written, []);
  assert.equal(hub.calls.length, 1);
  assert.equal(fs.existsSync(lock), false);
});

test('pull reports a failed request and releases the lock', async () => {
  const home = fs.mkdtempSync(path.join(os.tmpdir(), 'agenthub-pull-'));
  const hub = fakeHub([{ status: 503 }]);
  await assert.rejects(pullSkills(pullEnv(home), { fetch: hub.fetchImpl }), /503/);
  assert.equal(fs.existsSync(path.join(home, '.claude', '.agenthub-skills-sync.lock')), false);
});

test('pull does nothing without a callback', async () => {
  const result = await pullSkills({ HOME: '/nonexistent' }, { fetch: () => { throw new Error('no'); } });
  assert.equal(result.skippedReason, 'not configured');
});

test('links ~/.agents/skills to ~/.claude/skills and respects a directory of its own', () => {
  const home = fs.mkdtempSync(path.join(os.tmpdir(), 'agenthub-link-'));
  assert.equal(linkAgentsSkills(home), 'linked');
  assert.equal(linkAgentsSkills(home), 'linked');
  fs.mkdirSync(path.join(home, '.claude', 'skills', 'deploy'));
  fs.writeFileSync(path.join(home, '.claude', 'skills', 'deploy', 'SKILL.md'), '# deploy');
  assert.equal(fs.readFileSync(path.join(home, '.agents', 'skills', 'deploy', 'SKILL.md'), 'utf8'), '# deploy');

  const other = fs.mkdtempSync(path.join(os.tmpdir(), 'agenthub-link-'));
  fs.mkdirSync(path.join(other, '.agents', 'skills', 'theirs'), { recursive: true });
  assert.equal(linkAgentsSkills(other), 'kept-existing');
  assert.equal(fs.lstatSync(path.join(other, '.agents', 'skills')).isDirectory(), true);
});

test('pull clears what a pull that died mid-write left in staging', async () => {
  const home = fs.mkdtempSync(path.join(os.tmpdir(), 'agenthub-pull-'));
  const leftover = path.join(home, '.claude', '.agenthub-skills-staging', 'deploy.new-1-abcd');
  fs.mkdirSync(leftover, { recursive: true });
  const hub = fakeHub([{ status: 304 }]);
  await pullSkills(pullEnv(home), { fetch: hub.fetchImpl });
  assert.equal(fs.existsSync(leftover), false);
});
