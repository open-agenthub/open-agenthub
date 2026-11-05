// Just enough tar for one conversation's files. The archive comes from the hub's
// SessionLocalTransfer (pax, regular files) and goes back to it, so the reader handles pax and GNU
// long names and the writer emits GNU long names; anything else is refused by the caller.
// No dependency on purpose: the stdio server is run from a checkout, and every package it needs is
// one more `npm install` that has to have happened on the user's machine.

const BLOCK = 512;
const decoder = new TextDecoder();
const encoder = new TextEncoder();

/** @returns {{ name: string, type: string, mode: number, mtime: number, data: Uint8Array }[]} */
export function readTar(buffer) {
  const bytes = new Uint8Array(buffer);
  const entries = [];
  let offset = 0;
  let pendingName = null;
  while (offset + BLOCK <= bytes.length) {
    const header = bytes.subarray(offset, offset + BLOCK);
    if (header.every(byte => byte === 0)) break;
    offset += BLOCK;
    const size = octal(header, 124, 12);
    const type = String.fromCharCode(header[156] || 0x30);
    const content = bytes.subarray(offset, offset + size);
    if (content.length < size) throw new Error('tar_truncated');
    offset += Math.ceil(size / BLOCK) * BLOCK;
    if (type === 'L') { pendingName = cut(decoder.decode(content)); continue; }
    if (type === 'x') { pendingName = pax(decoder.decode(content), 'path') ?? pendingName; continue; }
    if (type === 'g' || type === 'K') continue;
    const prefix = cut(decoder.decode(header.subarray(345, 500)));
    const stored = cut(decoder.decode(header.subarray(0, 100)));
    entries.push({
      name: pendingName ?? (prefix ? `${prefix}/${stored}` : stored),
      type,
      mode: octal(header, 100, 8),
      mtime: octal(header, 136, 12),
      data: content.slice()
    });
    pendingName = null;
  }
  return entries;
}

export function writeTar(entries) {
  const parts = [];
  for (const entry of entries) {
    const name = encoder.encode(entry.name);
    if (name.length > 100) {
      // A sidecar path (.claude/projects/<folder>/<uuid>/subagents/<uuid>.jsonl) passes 100 bytes
      // with no single component long enough for ustar's prefix split, so GNU's long-name entry.
      const long = encoder.encode(`${entry.name}\0`);
      parts.push(header('././@LongLink', 'L', 0, 0, long.length), pad(long));
    }
    parts.push(header(decoder.decode(name.subarray(0, 100)), '0', entry.mode ?? 0o600, entry.mtime, entry.data.length));
    parts.push(pad(entry.data));
  }
  parts.push(new Uint8Array(BLOCK * 2));
  return concat(parts);
}

function header(name, type, mode, mtime, size) {
  const block = new Uint8Array(BLOCK);
  put(block, 0, 100, name);
  num(block, 100, 8, mode);
  num(block, 108, 8, 0);
  num(block, 116, 8, 0);
  num(block, 124, 12, size);
  num(block, 136, 12, mtime || Math.floor(Date.now() / 1000));
  block[156] = type.charCodeAt(0);
  put(block, 257, 8, 'ustar  ');
  block.fill(0x20, 148, 156);
  const sum = block.reduce((total, byte) => total + byte, 0);
  put(block, 148, 8, `${sum.toString(8).padStart(6, '0')}\0 `);
  return block;
}

function put(block, at, length, value) { block.set(encoder.encode(value).subarray(0, length), at); }
function num(block, at, length, value) { put(block, at, length - 1, Math.trunc(value).toString(8).padStart(length - 1, '0')); }
function octal(block, at, length) {
  const raw = cut(decoder.decode(block.subarray(at, at + length))).trim();
  return raw ? parseInt(raw, 8) || 0 : 0;
}
function cut(value) { const end = value.indexOf('\0'); return end < 0 ? value : value.slice(0, end); }
function pax(record, key) {
  for (const line of record.split('\n')) {
    const pair = line.slice(line.indexOf(' ') + 1);
    if (pair.startsWith(`${key}=`)) return pair.slice(key.length + 1);
  }
  return null;
}
function pad(data) {
  const out = new Uint8Array(Math.ceil(data.length / BLOCK) * BLOCK);
  out.set(data);
  return out;
}
function concat(parts) {
  const out = new Uint8Array(parts.reduce((total, part) => total + part.length, 0));
  let at = 0;
  for (const part of parts) { out.set(part, at); at += part.length; }
  return out;
}
