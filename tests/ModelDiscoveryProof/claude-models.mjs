// Ask the real Claude Code for its models through the SDK control protocol (stream-json).
import { spawn } from 'node:child_process';
const p = spawn(process.argv[2] || 'claude', ['-p', '--input-format', 'stream-json', '--output-format', 'stream-json', '--verbose'], { env: process.env, stdio: ['pipe', 'pipe', 'pipe'] });
let buf = '', err = ''; const t = setTimeout(() => { console.log('TIMEOUT'); p.kill(); }, 40000);
p.stderr.on('data', d => err += d);
p.on('exit', c => { if (err) console.log('[stderr]', err.slice(0, 600)); console.log('[exit]', c); });
p.stdout.on('data', d => { buf += d; let i; while ((i = buf.indexOf('\n')) >= 0) { const line = buf.slice(0, i); buf = buf.slice(i + 1);
  let m; try { m = JSON.parse(line); } catch { console.log('<<raw', line.slice(0, 200)); continue; }
  if (m.type === 'control_response') { const r = m.response.response || {}; console.log("MODELS", JSON.stringify(r.models)); console.log("ACCOUNT", JSON.stringify(r.account)); console.log("KEYS", Object.keys(r).join(",")); clearTimeout(t); setTimeout(() => p.kill(), +(process.env.LINGER || 0)); }
  else console.log('<<', m.type, m.subtype || '');
}});
p.stdin.write(JSON.stringify({ type: 'control_request', request_id: 'r1', request: { subtype: 'initialize' } }) + '\n');
