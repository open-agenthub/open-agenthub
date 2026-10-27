'use strict';
// Replay each real PTY frame into both releases so timing and auth randomness cannot
// masquerade as a renderer regression. Keep terminal bytes and auth values in memory.
const assert = require('node:assert/strict');
const fs = require('node:fs');
const os = require('node:os');
const path = require('node:path');
const { pathToFileURL } = require('node:url');
const pty = require(process.env.PROOF_PTY || '/opt/session-agent/node_modules/node-pty');
const { detectSignIn } = require('../../../agent-runtime/common/cli-sign-in');
const { createSignInPrivacy } = require('../../../agent-runtime/common/sign-in-privacy');
const baseline = require(process.env.PROOF_BASELINE || '/opt/session-agent/node_modules/@xterm/headless').Terminal;
const candidate = require('../../../agent-runtime/session-agent/node_modules/@xterm/headless').Terminal;
const sleep = ms => new Promise(resolve => setTimeout(resolve, ms));
(async () => {
 const { detectMenu } = await import(pathToFileURL(path.resolve(__dirname, '../../../frontend/src/lib/cli-menu.js')));
 const home = fs.mkdtempSync(path.join(os.tmpdir(), 'headless-compare-'));
 const terms = [baseline, candidate].map(Terminal => new Terminal({cols:120, rows:40, allowProposedApi:true, scrollback:0}));
 const privacy = [baseline, candidate].map(Terminal => createSignInPrivacy({Terminal}));
 const env = Object.fromEntries(Object.entries(process.env).filter(([key]) => !/^(CLAUDE|AGENTHUB|ANTHROPIC|OPENAI|CODEX|OPENCODE|CURSOR|OPENCLAW|XDG_|.*TOKEN|.*KEY|.*SECRET)/.test(key)));
 Object.assign(env, {HOME:home, XDG_CONFIG_HOME:home+'/.config', XDG_DATA_HOME:home+'/.local/share', XDG_STATE_HOME:home+'/.local/state', TERM:'xterm-256color', DISABLE_AUTOUPDATER:'1', OPENCODE_DISABLE_AUTOUPDATE:'1'});
 let child, pending = Promise.resolve(), frames = 0;
 const rows = terminal => {const b=terminal.buffer.active;return Array.from({length:terminal.rows},(_,i)=>b.getLine(b.baseY+i)?.translateToString(true)||'');};
 const args = JSON.parse(process.env.PROOF_ARGS || '[]');
 try {
  child = pty.spawn(process.env.PROOF_CLI || '/usr/local/bin/codex', args, {cwd:home,env,cols:120,rows:40,name:'xterm-256color'});
  terms[0].onData(data => child.write(data));
  child.onData(data => {pending=pending.then(async()=>{
   await Promise.all(terms.map(t=>new Promise(resolve=>t.write(data,resolve))));
   await Promise.all(privacy.map(p=>p.feed(data)));
   frames++;
  });});
  for (const step of JSON.parse(process.env.PROOF_STEPS || '[{"wait":6000}]')) {
   await sleep(step.wait || 200);await pending;
   if(step.keys)child.write(step.keys);
  }
  await pending;
  await Promise.all(privacy.map(p=>p.flush()));
  assert.equal(privacy[1].scrollback(),privacy[0].scrollback(), 'privacy export differs');
  const screens=terms.map(rows);
  // Report only fixed classifications, never the sign-in URL, device code, or screen.
  assert.deepEqual(screens[1],screens[0], 'visible cells differ');
  assert.deepEqual(detectSignIn(screens[1]),detectSignIn(screens[0]), 'sign-in classification differs');
  assert.deepEqual(detectMenu(screens[1]),detectMenu(screens[0]), 'menu classification differs');
  assert.ok(frames>0,'CLI emitted no frames');
  const kind=detectMenu(screens[0])?.kind || 'none';
  if(process.env.PROOF_EXPECT)assert.equal(kind,process.env.PROOF_EXPECT);
  console.log(JSON.stringify({frames,equalCells:true,equalSignIn:true,equalMenu:true,kind}));
 } finally {
  try{child?.kill();}catch{}
  await sleep(200);await pending;
  terms.forEach(t=>t.dispose());
  fs.rmSync(home,{recursive:true,force:true});
 }
})().catch(error=>{console.error('Comparison failed; terminal and authorization details withheld');process.exitCode=1;});
