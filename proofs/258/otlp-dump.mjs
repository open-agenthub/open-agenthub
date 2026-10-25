// Prints every metric data point in raw OTLP/HTTP protobuf bodies (as recorded by mock-model.mjs):
// metric name, value and attributes, resource attributes once per body.
//   node otlp-dump.mjs <file.bin>...
import fs from 'node:fs';

function* fields(buf) {
  let i = 0;
  const varint = () => { let r = 0n, s = 0n, b; do { b = buf[i++]; r |= BigInt(b & 0x7f) << s; s += 7n; } while (b & 0x80); return r; };
  while (i < buf.length) {
    const tag = Number(varint()); const f = tag >> 3, w = tag & 7;
    if (w === 0) yield [f, w, varint()];
    else if (w === 1) { yield [f, w, buf.subarray(i, i + 8)]; i += 8; }
    else if (w === 2) { const n = Number(varint()); yield [f, w, buf.subarray(i, i + n)]; i += n; }
    else if (w === 5) { yield [f, w, buf.subarray(i, i + 4)]; i += 4; }
    else throw new Error('wire ' + w);
  }
}
const str = b => Buffer.from(b).toString('utf8');
function anyValue(b) { for (const [f, w, v] of fields(b)) { if (f === 1) return str(v); if (f === 2 || f === 3) return String(v); if (f === 4) return Buffer.from(v).readDoubleLE(0); } return null; }
function kv(b) { let k, v; for (const [f, , x] of fields(b)) { if (f === 1) k = str(x); if (f === 2) v = anyValue(x); } return [k, v]; }
function point(b) { const attrs = {}; let value; for (const [f, , x] of fields(b)) { if (f === 4) value = Buffer.from(x).readDoubleLE(0); if (f === 6) value = Number(Buffer.from(x).readBigInt64LE(0)); if (f === 7) { const [k, v] = kv(x); attrs[k] = v; } } return { value, attrs }; }

for (const file of process.argv.slice(2)) {
  for (const [f, , rm] of fields(fs.readFileSync(file))) {
    if (f !== 1) continue;
    for (const [g, , x] of fields(rm)) {
      if (g === 1) { const res = {}; for (const [h, , a] of fields(x)) if (h === 1) { const [k, v] = kv(a); res[k] = v; } console.log('resource', JSON.stringify(res)); }
      if (g !== 2) continue;
      for (const [h, , m] of fields(x)) {
        if (h !== 2) continue;
        let name; const pts = [];
        for (const [i, , y] of fields(m)) { if (i === 1) name = str(y); if (i === 5 || i === 7) for (const [j, , p] of fields(y)) if (j === 1) pts.push(point(p)); }
        for (const p of pts) console.log(name, p.value, JSON.stringify(p.attrs));
      }
    }
  }
}
