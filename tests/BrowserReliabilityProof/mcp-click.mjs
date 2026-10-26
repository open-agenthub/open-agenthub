// #237: drives the REAL in-pod browser MCP (agent-runtime/browser/server.mjs) over stdio, as an
// agent CLI does, against a real Chromium, and checks after every failed or odd click that the
// next call still gets an answer instead of "Transport closed".
// usage: node mcp-click.mjs <session-agent dir with node_modules> <browser dir> [cdp endpoint]
// PROOF_SCENARIOS picks scenarios; with kill or freeze, PROOF_REPLACEMENT_CDP is the endpoint of a
// second, already running browser that stands in for the replacement pod.
import { cpSync, mkdtempSync, readFileSync, readdirSync, readlinkSync, symlinkSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { createRequire } from 'node:module';
import { startFixtures } from './fixtures.mjs';

const [sessionAgent, browserDir, cdp = 'http://127.0.0.1:9223'] = process.argv.slice(2);
const require = createRequire(join(sessionAgent, 'package.json'));
const { Client } = require('@modelcontextprotocol/sdk/client/index.js');
const { StdioClientTransport } = require('@modelcontextprotocol/sdk/client/stdio.js');

// The image layout: /opt/session-agent/{node_modules,browser}.
const opt = mkdtempSync(join(tmpdir(), 'session-agent-'));
symlinkSync(join(sessionAgent, 'node_modules'), join(opt, 'node_modules'));
cpSync(browserDir, join(opt, 'browser'), { recursive: true });

const fixtures = await startFixtures();
fixtures.setCdp(cdp);
let stderr = '';
const transport = new StdioClientTransport({
  command: process.execPath, args: [join(opt, 'browser', 'server.mjs')],
  env: { ...process.env, AGENTHUB_CALLBACK_URL: fixtures.callback, AGENTHUB_CALLBACK_TOKEN: 't' },
  stderr: 'pipe'
});
transport.stderr?.on('data', chunk => { stderr += chunk; });
let closed = false;
const client = new Client({ name: 'proof', version: '1' });
client.onclose = () => { closed = true; };
await client.connect(transport);

const call = async (name, args = {}) => {
  const started = Date.now();
  try {
    const result = await client.callTool({ name, arguments: args }, undefined, { timeout: 120_000 });
    const text = result.content?.find(c => c.type === 'text')?.text ?? `[${result.content?.[0]?.type}]`;
    return { ok: !result.isError, text: text.slice(0, 160), ms: Date.now() - started };
  } catch (error) {
    return { ok: false, text: `THROWN: ${error.message}`, ms: Date.now() - started };
  }
};
const refFor = async id => {
  const result = await client.callTool({ name: 'browser_snapshot', arguments: {} });
  const snapshot = result.isError ? {} : JSON.parse(result.content[0].text);
  const labels = { nav: 'navigate slowly', 'close-self': 'close self', replace: 'detach self', busy: 'busy', popup: 'popup',
    swap: 'cross site', frames: 'frames', download: 'download', file: 'upload', alert: 'alert', 'confirm-nav': 'leave dirty' };
  return snapshot.elements?.find(element => element.name === labels[id])?.ref;
};

// The browser's main process on the first endpoint's port, found by executable first: a pattern
// match on every /proc/N/cmdline from here could hit this script's own shell.
// SIGKILL closes its sockets; SIGSTOP leaves them open with nobody answering, as a browser pod
// that vanished with its node does.
function signalBrowser(signal) {
  for (const pid of readdirSync('/proc').filter(name => /^\d+$/.test(name))) {
    try {
      if (!readlinkSync(`/proc/${pid}/exe`).endsWith('/chromium')) continue;
      const cmdline = readFileSync(`/proc/${pid}/cmdline`, 'utf8');
      if (cmdline.includes('--type=') || !cmdline.includes(`--remote-debugging-port=${new URL(cdp).port}`)) continue;
      process.kill(Number(pid), signal);
      console.log(signal, 'browser pid', pid);
    } catch {}
  }
}

const results = [];
console.log('start', JSON.stringify(await call('browser_start')));
const scenarios = (process.env.PROOF_SCENARIOS ??
  'nav,busy,close-self,replace,popup,download,file,alert,confirm-nav').split(',');
for (const id of scenarios) {
  console.log('navigate', JSON.stringify(await call('browser_navigate', { url: `${fixtures.base}/` })));
  const lost = { kill: 'SIGKILL', freeze: 'SIGSTOP' }[id];
  const ref = await refFor(lost ? 'busy' : id);
  if (lost) setTimeout(() => signalBrowser(lost), 2_000);
  const click = await call('browser_click', { ref });
  let restart;
  // The backend has replaced the pod by now: browser_start hands out the new endpoint.
  if (lost && process.env.PROOF_REPLACEMENT_CDP) {
    fixtures.setCdp(process.env.PROOF_REPLACEMENT_CDP);
    restart = await call('browser_start');
  }
  const next = await call('browser_navigate', { url: `${fixtures.base}/target` });
  const row = { scenario: id, click, restart, next, transportClosed: closed };
  results.push(row);
  console.log(JSON.stringify(row));
  if (closed) break;
}

const broken = results.filter(r => r.transportClosed || !r.next.ok);
console.log(JSON.stringify({ verdict: broken.length ? 'FAIL' : 'PASS', broken: broken.map(r => r.scenario) }));
if (stderr) console.log('server stderr:\n' + stderr.slice(-4000));
await client.close().catch(() => undefined);
fixtures.server.close();
process.exit(0);
