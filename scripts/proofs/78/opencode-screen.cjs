'use strict';
// Drive the same pinned native binary under each host Node version, including terminal
// query replies: --version alone misses PTY ABI and interactive startup failures.
const assert = require('node:assert/strict');
const fs = require('node:fs');
const os = require('node:os');
const path = require('node:path');
const pty = require(process.env.PROOF_PTY || '/opt/session-agent/node_modules/node-pty');
const {Terminal} = require(process.env.PROOF_XTERM || '/opt/session-agent/node_modules/@xterm/headless');
const {detectSignIn} = require('../../../agent-runtime/common/cli-sign-in');
const home=fs.mkdtempSync(path.join(os.tmpdir(),'node-opencode-screen-'));
const terminal=new Terminal({cols:120,rows:40,allowProposedApi:true,scrollback:0});
const env={HOME:home,TMPDIR:home,PATH:process.env.PATH,XDG_CONFIG_HOME:home+'/.config',XDG_DATA_HOME:home+'/.local/share',TERM:'xterm-256color',OPENCODE_DISABLE_AUTOUPDATE:'1'};
let child, pending=Promise.resolve();
const rows=()=>{const b=terminal.buffer.active;return Array.from({length:terminal.rows},(_,i)=>b.getLine(b.baseY+i)?.translateToString(true)||'');};
const wait=ms=>new Promise(resolve=>setTimeout(resolve,ms));
async function ready(text){const end=Date.now()+30000;while(!rows().join('\n').includes(text)&&Date.now()<end)await wait(100);await pending;assert.ok(rows().join('\n').includes(text),'Native screen did not appear');}
(async()=>{
 try {
  assert.ok(process.env.PROOF_OPENCODE,'Set PROOF_OPENCODE to the exact runtime pin');
  child=pty.spawn(process.env.PROOF_OPENCODE,['auth','login'],{cwd:home,env,name:'xterm-256color',cols:120,rows:40});
  terminal.onData(data=>child.write(data));
  child.onData(data=>{pending=pending.then(()=>new Promise(resolve=>terminal.write(data,resolve)));});
  await ready('Select provider');child.write('\r');await ready('Enter your API key');
  const signIn=detectSignIn(rows());assert.equal(signIn?.kind,'signin');assert.equal(signIn?.mode,'credential');
  console.log(JSON.stringify({node:process.version,pin:'1.18.35',providerSelector:true,credentialScreen:true,signInMode:signIn.mode}));
 }finally{try{child?.kill();}catch{}await wait(200);await pending;terminal.dispose();fs.rmSync(home,{recursive:true,force:true});}
})().catch(()=>{console.error('Interactive proof failed; terminal details withheld');process.exitCode=1;});
