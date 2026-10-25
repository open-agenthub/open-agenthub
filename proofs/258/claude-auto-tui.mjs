// Which credential the REAL interactive Claude Code TUI uses when the pod holds both a login and
// ANTHROPIC_API_KEY (an Auto session). Real node-pty + @xterm/headless; the home is prepared by the
// runtime's own workspace-trust.mjs, as the entrypoint does. Fake credential values only.
//   node claude-auto-tui.mjs <home> <workdir> <mockPort> <wt> [withKey=1]
import fs from 'node:fs';
import { execFileSync } from 'node:child_process';
import { createRequire } from 'node:module';
const require = createRequire(import.meta.url);
const pty = require('/opt/session-agent/node_modules/node-pty');
const { Terminal } = require(process.env.XTERM || '@xterm/headless');

const [home, work, port, wt, withKey = '1'] = process.argv.slice(2);
fs.mkdirSync(home + '/.claude', { recursive: true });
fs.writeFileSync(home + '/.claude/.credentials.json', JSON.stringify({ claudeAiOauth: {
  accessToken: 'sk-ant-oat01-rig', refreshToken: 'sk-ant-ort01-rig', expiresAt: Date.now() + 86_400_000,
  scopes: ['user:inference', 'user:profile'], subscriptionType: 'max' } }));
fs.writeFileSync(home + '/.claude.json', JSON.stringify({ hasCompletedOnboarding: true, theme: 'dark' }));
execFileSync('node', [wt + '/agent-runtime/claude/workspace-trust.mjs', home + '/.claude.json', work]);

const env = { PATH: '/usr/bin:/bin:/usr/local/bin', HOME: home, TERM: 'xterm-256color',
  ANTHROPIC_BASE_URL: 'http://127.0.0.1:' + port };
if (withKey === '1') env.ANTHROPIC_API_KEY = 'sk-ant-api03-rigrigrig';
const term = new Terminal({ cols: 120, rows: 40, allowProposedApi: true });
const p = pty.spawn('/usr/local/bin/claude', [], { cwd: work, env, cols: 120, rows: 40 });
p.onData(d => term.write(d));
term.onData(d => p.write(d));
const screen = () => { const b = term.buffer.active; const out = []; for (let i = 0; i < b.length; i++) out.push(b.getLine(i).translateToString(true)); return out.join('\n').replace(/\n+$/, ''); };
const sleep = ms => new Promise(r => setTimeout(r, ms));
await sleep(8000);
console.log('--- screen after start\n' + screen().split('\n').filter(Boolean).slice(-25).join('\n'));
p.write('say hi'); await sleep(400); p.write('\r'); await sleep(8000);
console.log('--- screen after turn\n' + screen().split('\n').filter(Boolean).slice(-15).join('\n'));
p.kill(); process.exit(0);
