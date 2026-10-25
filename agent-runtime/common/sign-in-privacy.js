'use strict';
const { detectSignIn } = require('./cli-sign-in');

// Export a separate buffer: whole auth screens are omitted so wrapped URLs, partial codes,
// and redraws cannot slip into an upload between terminal writes.
function createSignInPrivacy({ Terminal, maxBuffer = 1_000_000 } = {}) {
  // Images install modules beside common/; source checkouts install them in session-agent/.
  Terminal ||= require(require.resolve('@xterm/headless', {
    paths: [__dirname, require('node:path').join(__dirname, '..', 'session-agent')]
  })).Terminal;
  const terminal = new Terminal({ cols: 120, rows: 32, allowProposedApi: true, scrollback: 0 });
  const secrets = new Set();
  let exported = '', active = false, observed = false, input = '';
  let revision = 0;
  let pending = Promise.resolve();
  let queued = 0;
  function remember(value) {
    if (typeof value === 'string' && value.length >= 4) {
      if (!secrets.has(value)) revision += 1;
      secrets.add(value);
      secrets.add(JSON.stringify(value).slice(1, -1));
    }
  }
  function feed(data) {
    queued += 1;
    pending = pending.then(() => new Promise(resolve => terminal.write(data, () => {
      const buffer = terminal.buffer.active;
      const rows = Array.from({ length: terminal.rows }, (_, y) => buffer.getLine(buffer.baseY + y)?.translateToString(true) || '');
      const signIn = detectSignIn(rows);
      const intro = /Select login method:|Starting login process|Follow these steps to sign in|Add credential|Token provider/.test(rows.join('\n'));
      const wasActive = active;
      // TUIs clear and redraw a dialog in separate writes, especially after a resize. A blank
      // intermediate frame is not the end of sign-in: release only at a real CLI prompt.
      const ready = rows.filter(row => row.trim()).slice(-10).some(row => /^\s*[❯›>]\s*(?:Ask (?:Codex|anything).*|)$/.test(row)) || /local ready/.test(rows.filter(row => row.trim()).slice(-10).join('\n'));
      active = Boolean(signIn || (!ready && (intro || wasActive)));
      if (active) {
        if (!wasActive) revision += 1;
        observed = true;
        if (signIn?.url) remember(signIn.url);
        if (signIn?.code) remember(signIn.code);
        let continuation = false;
        for (const row of rows) {
          const match = /https?:\/\/\S+/.exec(row);
          if (match) { remember(match[0]); continuation = true; }
          else if (continuation && /^\S+$/.test(row.trim())) {
            // Public route words can wrap onto their own row. Remembering "login" as a
            // secret also matches ordinary /login command metadata and blocks every archive.
            const fragment = row.trim();
            const publicRoute = /^(login|authorize|oauth|device|auth)$/i.test(fragment);
            if (!publicRoute) remember(fragment);
          }
          else continuation = false;
        }
      }
      if (wasActive && !active) input = '';
      if (active || wasActive) {
        if (!wasActive) exported += '\r\n[Sign-in terminal output omitted]\r\n';
      } else exported += data;
      exported = exported.slice(-maxBuffer);
      queued -= 1;
      resolve();
    })));
  }
  function personInput(data) {
    if (!active) return;
    const value = data.replace(/\x1b\[20[01]~/g, '');
    // Capture the whole paste before the CLI can echo it or write a transcript entry.
    if (data.startsWith('\x1b[200~')) remember(value);
    for (const character of value) {
      if (character === '\r' || character === '\n') { remember(input); input = ''; }
      else if (character === '\x7f' || character === '\b') input = input.slice(0, -1);
      else if (character >= ' ' && character !== '\x7f') input = (input + character).slice(-8192);
    }
  }
  function mask(text) {
    if (typeof text !== 'string' || !observed) return text;
    for (const secret of [...secrets].sort((a, b) => b.length - a.length)) text = text.split(secret).join('[sign-in data removed]');
    return text;
  }
  // Native diagnostics can contain authorization data too. Withhold an archive that contains
  // it rather than modifying the CLI's originals and corrupting its resume state.
  function scan(root, fs, excludes, found, optional = false) {
    const needles = [...secrets].map(value => Buffer.from(value));
    const overlap = Math.max(1, ...needles.map(value => value.length));
    function visit(file) {
      const relative = require('node:path').relative(require('node:path').dirname(root), file).split(require('node:path').sep).join('/');
      const excluded = excludes.some(pattern => {
        const expression = pattern.split('*').map(part => part.replaceAll('.', '[.]')).join('.*');
        return new RegExp('^' + expression + '(?:/|$)').test(relative);
      });
      if (excluded) return true;
      let stat;
      try { stat = fs.lstatSync(file); } catch (error) { return optional && file === root && error.code === 'ENOENT'; }
      if (stat.isSymbolicLink()) return file !== root;
      if (stat.isDirectory()) {
        try { return fs.readdirSync(file).every(name => visit(require('node:path').join(file, name))); } catch { return false; }
      }
      if (!stat.isFile()) return true;
      let fd;
      try {
        const flags = require('node:fs').constants;
        fd = fs.openSync(file, flags.O_RDONLY | (flags.O_NOFOLLOW || 0) | (flags.O_NONBLOCK || 0));
        const opened = fs.fstatSync(fd);
        if (!opened.isFile() || opened.dev !== stat.dev || opened.ino !== stat.ino) return false;
        const chunk = Buffer.alloc(64 * 1024);
        let tail = Buffer.alloc(0), count;
        while ((count = fs.readSync(fd, chunk, 0, chunk.length, null)) > 0) {
          const data = Buffer.concat([tail, chunk.subarray(0, count)]);
          for (const needle of needles) if (data.includes(needle)) found.add(needle.toString());
          tail = data.subarray(Math.max(0, data.length - overlap));
        }
        const after = fs.fstatSync(fd);
        return after.size === opened.size && after.mtimeMs === opened.mtimeMs && after.ctimeMs === opened.ctimeMs;
      } catch { return false; }
      finally { if (fd !== undefined) fs.closeSync(fd); }
    }
    return visit(root);
  }
  function archiveSafe(root, fs = require('node:fs'), excludes = []) {
    if (active) return false;
    if (!secrets.size) return true;
    const found = new Set();
    return scan(root, fs, excludes, found) && found.size === 0;
  }
  // Time alone cannot establish that an authorization value is safe to forget. Native history
  // can outlive its login screen; keep each value until every possible export source is clean.
  function releaseAbsent(sources, texts = [], fs = require('node:fs')) {
    if (active || !secrets.size) return 0;
    const found = new Set();
    const buffer = terminal.buffer.active;
    const screen = Array.from({ length: terminal.rows }, (_, y) => buffer.getLine(buffer.baseY + y)?.translateToString(true) || '').join('\n');
    for (const text of [exported, input, screen, ...texts]) {
      for (const value of secrets) if (String(text || '').includes(value)) found.add(value);
    }
    for (const { root, excludes = [] } of sources) {
      if (!scan(root, fs, excludes, found, true)) return 0;
    }
    let released = 0;
    for (const value of secrets) if (!found.has(value)) { secrets.delete(value); released += 1; }
    return released;
  }
  return { feed, personInput, mask, archiveSafe, releaseAbsent, retained: () => secrets.size, flush: () => queued ? pending : null,
    scrollback: () => mask(exported), active: () => active, observed: () => observed, revision: () => revision,
    resize: (cols, rows) => { pending = pending.then(() => terminal.resize(cols, rows)); },
    dispose: () => { void pending.then(() => terminal.dispose()); } };
}
module.exports = { createSignInPrivacy };
