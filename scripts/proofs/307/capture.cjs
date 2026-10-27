'use strict';
// Capture only an isolated synthetic startup, never a signed-in user's terminal.
const fs = require('node:fs');
const os = require('node:os');
const path = require('node:path');
const assert = require('node:assert/strict');
const pty = require('/opt/session-agent/node_modules/node-pty');
const {Terminal} = require('/tmp/proof-tools/node_modules/@xterm/headless');
const home = fs.mkdtempSync(path.join(os.tmpdir(), 'startup-proof-'));
const key = 'sk-ant-api03-' + 'x'.repeat(80);
fs.writeFileSync(home+'/.claude.json', JSON.stringify({hasCompletedOnboarding:true,theme:'dark',customApiKeyResponses:{approved:[key.slice(-20)],rejected:[]},projects:{[home]:{hasTrustDialogAccepted:true,hasCompletedProjectOnboarding:true}}}));
const env = {PATH:process.env.PATH,HOME:home,TERM:'xterm-256color',LANG:'C.UTF-8',ANTHROPIC_API_KEY:key,ANTHROPIC_BASE_URL:'http://127.0.0.1:1',CLAUDE_CODE_DISABLE_NONESSENTIAL_TRAFFIC:'1',DISABLE_AUTOUPDATER:'1',FORCE_COLOR:'3'};
const terminal = new Terminal({cols:120,rows:40,allowProposedApi:true});
const child = pty.spawn('/tmp/proof-tools/node_modules/@anthropic-ai/claude-code/bin/claude.exe', [], {cwd:home,env,cols:120,rows:40,name:'xterm-256color'});
let output = '', pending = Promise.resolve();
terminal.onData(data=>child.write(data));
child.onData(data=>{output += data;pending=pending.then(()=>new Promise(resolve=>terminal.write(data,resolve)));});
setTimeout(async()=>{
 await pending;
 const b=terminal.buffer.active;
 const screen=Array.from({length:terminal.rows},(_,i)=>b.getLine(b.baseY+i)?.translateToString(true)||'').join('\n');
 try {
  assert.ok(screen.includes('auto mode on'), 'real startup did not reach ready prompt');
  const fixture=path.resolve(__dirname,'../../../tests/AgentHub.Api.Tests/Fixtures/claude-startup.json');
  fs.writeFileSync(fixture,JSON.stringify({version:'2.1.287',cols:120,rows:40,output:output.split(home).join('/tmp/startup-proof'),screen:screen.split(home).join('/tmp/startup-proof')},null,2)+'\n');
  console.log('Real Claude startup captured; cursor-forward sequences:',(output.match(/\x1b\[\d*C/g)||[]).length);
 } catch(error) {console.error(screen);process.exitCode=1;}
 finally {child.kill();terminal.dispose();}
},12000);
