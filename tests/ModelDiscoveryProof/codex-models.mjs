// Ask the real `codex app-server` for model/list over its stdio JSON-RPC.
import { spawn } from 'node:child_process';
const bin = process.argv[2];
const p = spawn(bin, ['app-server'], { env: process.env, stdio: ['pipe', 'pipe', 'pipe'] });
let buf = ''; const t = setTimeout(() => { console.log('TIMEOUT'); p.kill(); }, 30000);
p.stderr.on('data', d => process.stderr.write('[stderr] ' + d));
p.stdout.on('data', d => { buf += d; let i; while ((i = buf.indexOf('\n')) >= 0) { const line = buf.slice(0, i); buf = buf.slice(i + 1);
  console.log('<<', line.slice(0, 4000));
  const msg = JSON.parse(line);
  if (msg.id === 1) { p.stdin.write(JSON.stringify({ method: 'initialized' }) + '\n'); p.stdin.write(JSON.stringify({ id: 2, method: process.env.METHOD || 'model/list', params: JSON.parse(process.env.PARAMS || '{}') }) + '\n'); }
  if (msg.id === 2) { clearTimeout(t); setTimeout(() => p.kill(), +(process.env.LINGER||0)); }
}});
p.stdin.write(JSON.stringify({ id: 1, method: 'initialize', params: { clientInfo: { name: 'probe', version: '0' } } }) + '\n');
