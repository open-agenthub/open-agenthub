'use strict';
// A version print does not prove a TUI, but catches launcher/engine failures before
// running the separate real-screen comparisons. Never inspect a signed-in home.
const assert = require('node:assert/strict');
const {execFileSync} = require('node:child_process');
const fs = require('node:fs');
const os = require('node:os');
const path = require('node:path');
const pty = require(process.env.PROOF_PTY || '/opt/session-agent/node_modules/node-pty');
const bins = process.env.PROOF_CLI_BIN;
assert.ok(bins, 'Set PROOF_CLI_BIN to the exact runtime CLI installation');
assert.ok(process.env.PROOF_CURSOR, 'Set PROOF_CURSOR to the installed unpinned executable');
const home = fs.mkdtempSync(path.join(os.tmpdir(), 'node-cli-smoke-'));
const env = {PATH:process.env.PATH, HOME:home, TMPDIR:home, XDG_CONFIG_HOME:home+'/.config', XDG_DATA_HOME:home+'/.local/share', DISABLE_AUTOUPDATER:'1', OPENCODE_DISABLE_AUTOUPDATE:'1', OPENCLAW_NO_AUTO_UPDATE:'1'};
(async()=>{
 try {
  for(const [name,executable,args,expected] of [
   ['claude',path.join(bins,'claude'),['--version'],'2.1.287'],
   ['codex',path.join(bins,'codex'),['--version'],'0.160.0'],
   ['opencode',path.join(bins,'opencode'),['--version'],'1.18.35'],
   ['openclaw',path.join(bins,'openclaw'),['--version'],'2026.7.1-2'],
   ['cursor',process.env.PROOF_CURSOR,['--disable-auto-update','--version'],null]
  ]) {
   const output=execFileSync(executable,args,{env,encoding:'utf8',timeout:20000}).trim();
   if(expected)assert.ok(output.includes(expected),name+' pin mismatch');
   else assert.match(output,/^\d{4}\.\d{2}\.\d{2}-[a-z0-9]+$/);
   console.log(JSON.stringify({agent:name,version:output,launcher:'passed',node:process.version}));
  }
  await new Promise((resolve,reject)=>{
   const child=pty.spawn(process.execPath,['-p','process.version'],{cwd:home,env,cols:80,rows:24,name:'xterm-256color'});
   let output='';const timer=setTimeout(()=>{child.kill();reject(Error('native PTY timeout'));},10000);
   child.onData(data=>output+=data);
   child.onExit(({exitCode})=>{clearTimeout(timer);try{assert.equal(exitCode,0);assert.equal(output.trim(),process.version);console.log(JSON.stringify({nativePty:'passed',node:process.version}));resolve();}catch(error){reject(error);}});
  });
 } finally {fs.rmSync(home,{recursive:true,force:true});}
})().catch(()=>{console.error('CLI/PTY compatibility failed');process.exitCode=1;});
