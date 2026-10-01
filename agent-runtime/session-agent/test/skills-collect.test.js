const assert = require('node:assert/strict');
const fs = require('node:fs');
const os = require('node:os');
const path = require('node:path');
const test = require('node:test');

function workdir() {
  const dir = fs.mkdtempSync(path.join(os.tmpdir(), 'skills-collect-'));
  test.after(() => fs.rmSync(dir, { recursive: true, force: true }));
  return dir;
}

function write(file, content) {
  fs.mkdirSync(path.dirname(file), { recursive: true });
  fs.writeFileSync(file, content);
}

test('a skill directory becomes SKILL.md plus its files', async () => {
  const { collectSource } = await import('../../skills/collect.mjs');
  const dir = workdir();
  write(path.join(dir, 'SKILL.md'), '---\nname: deploy\n---\n# Deploy');
  write(path.join(dir, 'scripts/check.sh'), '#!/bin/sh\necho größe 🚀');
  write(path.join(dir, 'reference.md'), '# docs');

  const collected = await collectSource(dir);

  assert.equal(collected.kind, 'directory');
  assert.match(collected.content, /# Deploy/);
  assert.deepEqual(collected.files.map(f => f.path), ['reference.md', 'scripts/check.sh']);
  assert.equal(collected.files.find(f => f.path === 'scripts/check.sh').content,
    '#!/bin/sh\necho größe 🚀');
  assert.deepEqual(collected.skipped, []);
});

test('a single script becomes one extra file, with no SKILL.md of its own', async () => {
  const { collectSource } = await import('../../skills/collect.mjs');
  const dir = workdir();
  // The case that started this: an 18 KB helper the agent would not quote into a tool call.
  const script = 'Write-Host "x"\n'.repeat(1200);
  write(path.join(dir, 'immo_rechner.ps1'), script);

  const collected = await collectSource(path.join(dir, 'immo_rechner.ps1'));

  assert.equal(collected.kind, 'file');
  assert.equal(collected.content, null);
  assert.deepEqual(collected.files.map(f => f.path), ['immo_rechner.ps1']);
  assert.equal(collected.files[0].content, script);
  assert.ok(collected.chars > 17_000);
});

test('a SKILL.md by itself is the skill content', async () => {
  const { collectSource } = await import('../../skills/collect.mjs');
  const dir = workdir();
  write(path.join(dir, 'SKILL.md'), '# only the doc');

  const collected = await collectSource(path.join(dir, 'SKILL.md'));

  assert.equal(collected.kind, 'skill-md');
  assert.equal(collected.content, '# only the doc');
  assert.deepEqual(collected.files, []);
});

test('files the backend would reject are reported, not dropped in silence', async () => {
  const { collectSource } = await import('../../skills/collect.mjs');
  const dir = workdir();
  write(path.join(dir, 'SKILL.md'), '# s');
  write(path.join(dir, 'a/b/c/deep.md'), 'too deep');
  write(path.join(dir, '.hidden'), 'dot file');
  write(path.join(dir, 'binary.bin'), Buffer.from([0x00, 0x01, 0x02]));
  write(path.join(dir, 'fine.md'), 'kept');

  const collected = await collectSource(dir);

  assert.deepEqual(collected.files.map(f => f.path), ['fine.md']);
  const reasons = collected.skipped.join(' | ');
  assert.match(reasons, /deep/);
  assert.match(reasons, /\.hidden/);
  assert.match(reasons, /binary\.bin/);
});

test('a missing path fails with the path in the message', async () => {
  const { collectSource } = await import('../../skills/collect.mjs');
  await assert.rejects(() => collectSource(path.join(workdir(), 'nope')),
    /skills_path_not_found: .*nope/);
});

test('a tar.gz is unpacked and read like a directory', async () => {
  const { collectSource } = await import('../../skills/collect.mjs');
  const dir = workdir();
  write(path.join(dir, 'skill/SKILL.md'), '# from archive');
  write(path.join(dir, 'skill/scripts/run.sh'), 'echo hi');
  const archive = path.join(dir, 'skill.tar.gz');
  require('node:child_process').execFileSync('tar', ['czf', 'skill.tar.gz', 'skill'], { cwd: dir });

  const collected = await collectSource(archive);

  assert.equal(collected.kind, 'archive');
  assert.equal(collected.content, '# from archive');
  assert.deepEqual(collected.files.map(f => f.path), ['scripts/run.sh']);
});

test('a bundle is unpacked into out_dir and every written path is reported', async () => {
  const { writeBundle } = await import('../../skills/collect.mjs');
  const dir = workdir();
  write(path.join(dir, 'src/SKILL.md'), '# bundled');
  write(path.join(dir, 'src/scripts/check.sh'), 'true');
  const archive = path.join(dir, 'src', 'bundle.tar.gz');
  require('node:child_process').execFileSync(
    'tar', ['czf', 'bundle.tar.gz', 'SKILL.md', 'scripts'], { cwd: path.join(dir, 'src') });

  const target = path.join(dir, 'out', 'nested');
  const result = await writeBundle(target, fs.readFileSync(archive));

  assert.equal(fs.readFileSync(path.join(target, 'SKILL.md'), 'utf8'), '# bundled');
  assert.equal(fs.readFileSync(path.join(target, 'scripts', 'check.sh'), 'utf8'), 'true');
  const relative = result.written.map(file => path.relative(target, file).split(path.sep).join('/')).sort();
  assert.deepEqual(relative, ['SKILL.md', 'scripts/check.sh']);
});
