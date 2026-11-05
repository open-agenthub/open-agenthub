const assert = require('node:assert/strict');
const fs = require('node:fs');
const os = require('node:os');
const path = require('node:path');
const { createRequire } = require('node:module');
const requireFrontend = createRequire(path.resolve(__dirname, '../../frontend/package.json'));
const requireBeautify = createRequire(requireFrontend.resolve('js-beautify'));
const editorconfig = requireBeautify('editorconfig');
const { glob } = requireBeautify('glob');
const beautify = requireFrontend('js-beautify');

async function main() {
  const root = fs.mkdtempSync(path.join(os.tmpdir(), 'minimatch-parents-'));
  try {
    fs.mkdirSync(path.join(root, 'src'));
    fs.writeFileSync(path.join(root, '.editorconfig'), 'root = true\n\n[src/*.{js,vue}]\nindent_style = space\nindent_size = 3\n\n[src/ignored.js]\nindent_size = 7\n');
    for (const name of ['component.vue', 'main.js', 'ignored.js', 'note.txt']) {
      fs.writeFileSync(path.join(root, 'src', name), '');
    }
    // Exercise both consumers' matching paths, including brace expansion and exclusions.
    const options = await editorconfig.parse(path.join(root, 'src/component.vue'));
    assert.equal(options.indent_size, 3);
    assert.equal((await editorconfig.parse(path.join(root, 'src/ignored.js'))).indent_size, 7);
    assert.equal((await editorconfig.parse(path.join(root, 'src/note.txt'))).indent_size, undefined);
    assert.deepEqual((await glob('src/**/*.{js,vue}', { cwd: root, ignore: '**/ignored.js' })).sort(), ['src/component.vue', 'src/main.js']);
    assert.equal(beautify.html('<div><span>ok</span></div>', { indent_size: options.indent_size }), '<div><span>ok</span></div>');
    assert.match(beautify.js('function test(){return 1;}', { indent_size: options.indent_size }), /\n {3}return 1;/);
    console.log('editorconfig, glob, and js-beautify compatibility passed');
  } finally {
    fs.rmSync(root, { recursive: true, force: true });
  }
}
main().catch(error => { console.error(error); process.exitCode = 1; });
