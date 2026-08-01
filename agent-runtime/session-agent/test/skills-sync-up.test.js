'use strict';

const assert = require('node:assert/strict');
const fs = require('node:fs');
const os = require('node:os');
const path = require('node:path');
const test = require('node:test');

const { materializeSkills } = require('../../common/skills');
const { collectLocalSkills, extractDescription, syncUp } = require('../../common/skills-sync-up');

function tempHome() {
  return fs.mkdtempSync(path.join(os.tmpdir(), 'agenthub-syncup-'));
}

function writeSkill(skillsDir, name, content, files = {}) {
  const dir = path.join(skillsDir, name);
  fs.mkdirSync(dir, { recursive: true });
  fs.writeFileSync(path.join(dir, 'SKILL.md'), content);
  for (const [relative, text] of Object.entries(files)) {
    const target = path.join(dir, relative);
    fs.mkdirSync(path.dirname(target), { recursive: true });
    fs.writeFileSync(path.join(target), text);
  }
}

test('collects only unmanaged skill directories, with description and files', () => {
  const home = tempHome();
  const skillsDir = path.join(home, '.claude', 'skills');
  // A hub-managed skill (recorded in the manifest) must NOT be reported back.
  materializeSkills({ skills: [{ name: 'managed', content: '# from hub' }] }, skillsDir);
  writeSkill(skillsDir, 'local-skill',
    '---\nname: local-skill\ndescription: "My local helper"\n---\n\nBody.',
    { 'scripts/run.sh': '#!/bin/sh\necho hi', 'binary.bin': Buffer.from([0, 1, 2]).toString('binary') });
  fs.writeFileSync(path.join(skillsDir, 'binary.bin'), Buffer.from([0, 1, 2]));

  const skills = collectLocalSkills(skillsDir);

  assert.equal(skills.length, 1);
  assert.equal(skills[0].name, 'local-skill');
  assert.equal(skills[0].description, 'My local helper');
  assert.match(skills[0].content, /Body\./);
  // The binary file was written via a lossy encoding and contains a null byte → skipped.
  assert.deepEqual(skills[0].files.map((f) => f.path), ['scripts/run.sh']);
});

test('ignores directories without SKILL.md and invalid names', () => {
  const home = tempHome();
  const skillsDir = path.join(home, '.claude', 'skills');
  fs.mkdirSync(path.join(skillsDir, 'no-skill-md'), { recursive: true });
  fs.mkdirSync(path.join(skillsDir, 'Bad Name'), { recursive: true });
  fs.writeFileSync(path.join(skillsDir, 'Bad Name', 'SKILL.md'), '# x');

  assert.deepEqual(collectLocalSkills(skillsDir), []);
});

test('extractDescription reads the frontmatter and tolerates its absence', () => {
  assert.equal(extractDescription('---\ndescription: Use for deploys\n---\nBody'), 'Use for deploys');
  assert.equal(extractDescription('# no frontmatter'), '');
});

test('syncUp posts the payload with the agent token and reports the result', async () => {
  const home = tempHome();
  writeSkill(path.join(home, '.claude', 'skills'), 'local-skill', '# local');

  let captured;
  const fetchImpl = async (url, options) => {
    captured = { url, options };
    return { ok: true, json: async () => ({ created: ['local-skill'], updated: [], unchanged: [], skipped: [] }) };
  };

  const { sent, result } = await syncUp(
    { HOME: home, AGENTHUB_CALLBACK_URL: 'http://hub/internal/sessions/s1', AGENTHUB_CALLBACK_TOKEN: 'tok' },
    { fetch: fetchImpl });

  assert.equal(sent, 1);
  assert.deepEqual(result.created, ['local-skill']);
  assert.equal(captured.url, 'http://hub/internal/sessions/s1/skills/import');
  assert.equal(captured.options.headers['X-Agent-Token'], 'tok');
  const body = JSON.parse(captured.options.body);
  assert.equal(body.skills[0].name, 'local-skill');
});

test('syncUp is a no-op without local skills or configuration', async () => {
  const home = tempHome();
  let called = false;
  const fetchImpl = async () => { called = true; return { ok: true, json: async () => ({}) }; };

  assert.deepEqual(await syncUp({ HOME: home }, { fetch: fetchImpl }), { sent: 0 });
  assert.deepEqual(
    await syncUp({ HOME: home, AGENTHUB_CALLBACK_URL: 'http://hub', AGENTHUB_CALLBACK_TOKEN: 't' },
      { fetch: fetchImpl }),
    { sent: 0 });
  assert.equal(called, false);
});
