'use strict';

const fs = require('node:fs');
const path = require('node:path');

// The hook's parent can be a shell or a launcher, rather than the PTY process itself.
// Record its ancestry so discovery can select only the CLI the session agent launched.
// Helper/title threads have no transcript and must never replace the user's conversation.
function recordConversation(payload, env = process.env) {
  if (!payload || typeof payload.transcript_path !== 'string' ||
      !/^[0-9a-f-]{36}$/i.test(payload.session_id || '')) return;
  const home = env.CODEX_HOME || path.join(env.HOME || '/home/agent', '.codex');
  const transcript = path.resolve(payload.transcript_path);
  if (!transcript.startsWith(path.join(home, 'sessions') + path.sep) ||
      !transcript.endsWith('-' + payload.session_id + '.jsonl')) return;
  const root = path.join(home, 'agenthub-active');
  fs.mkdirSync(root, { recursive: true });
  const record = { id: payload.session_id, file: transcript, cwd: payload.cwd, at: Date.now() };
  let pid = process.ppid;
  for (let depth = 0; pid > 1 && depth < 16; depth++) {
    const target = path.join(root, pid + '.json');
    let procStart = '';
    try { procStart = fs.readFileSync('/proc/' + pid + '/stat', 'utf8').split(') ').pop().split(' ')[19]; } catch {}
    let cleared = false;
    let previousFile;
    try {
      const previous = JSON.parse(fs.readFileSync(target, 'utf8'));
      cleared = previous.procStart === procStart && (previous.cleared === true || previous.id !== record.id);
      if (previous.procStart === procStart) previousFile = previous.id !== record.id ? previous.file : previous.previousFile;
    } catch {}
    fs.writeFileSync(target, JSON.stringify({ ...record, procStart, cleared, previousFile }), { mode: 0o600 });
    try {
      const status = fs.readFileSync('/proc/' + pid + '/status', 'utf8');
      pid = Number(/^PPid:\s+(\d+)/m.exec(status)?.[1] || 0);
    } catch { break; }
  }
}

if (require.main === module) {
  let input = '';
  process.stdin.on('data', chunk => { if (input.length < 1_000_000) input += chunk; });
  process.stdin.on('end', () => {
    try { recordConversation(JSON.parse(input)); } catch {}
  });
}

module.exports = { recordConversation };
