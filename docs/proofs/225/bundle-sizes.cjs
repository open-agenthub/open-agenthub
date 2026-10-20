const fs = require('node:fs');
const path = require('node:path');
const zlib = require('node:zlib');
function measure(root) {
  const files = fs.readdirSync(path.join(root, 'assets')).filter(n => n.endsWith('.js'));
  const html = fs.readFileSync(path.join(root, 'index.html'), 'utf8');
  const entry = html.match(/src="([^" ]+\.js)"/)[1];
  const sizes = names => names.reduce((s, n) => { const b = fs.readFileSync(path.join(root, n)); return { bytes: s.bytes + b.length, gzipBytes: s.gzipBytes + zlib.gzipSync(b).length }; }, { bytes: 0, gzipBytes: 0 });
  return { initialEntry: sizes([entry.replace(/^\//, '')]), allJavaScript: sizes(files.map(n => 'assets/' + n)), chunks: files.length };
}
const before = measure(process.argv[2]);
const after = measure(process.argv[3]);
console.log(JSON.stringify({ before, after, delta: { initialBytes: after.initialEntry.bytes - before.initialEntry.bytes, initialGzipBytes: after.initialEntry.gzipBytes - before.initialEntry.gzipBytes, allBytes: after.allJavaScript.bytes - before.allJavaScript.bytes, allGzipBytes: after.allJavaScript.gzipBytes - before.allJavaScript.gzipBytes } }, null, 2));
