'use strict';

const assert = require('node:assert/strict');
const fs = require('node:fs');
const os = require('node:os');
const path = require('node:path');
const test = require('node:test');

const { materializeSkills, MANIFEST } = require('../../common/skills');

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
