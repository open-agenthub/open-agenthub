'use strict';
// A real PTY drives the real native CLI inside the restricted proof pod over SSH.
// All identities and API credentials are synthetic; no host session environment is forwarded.
const fs = require('node:fs');
const path = require('node:path');
const assert = require('node:assert/strict');
const {execFileSync} = require('node:child_process');
const pty = require('/opt/session-agent/node_modules/node-pty');
const {Terminal} = require(process.env.PROOF_XTERM || '/tmp/proof-tools/node_modules/@xterm/headless');
const root = process.env.TASK_ROOT || '/tmp/proof-308-vm';
const evidence = fs.mkdtempSync(path.join(require('node:os').tmpdir(),'agenthub-cli-proof-'));
const ssh = ['-i',root+'/private/demo-ssh','-p','2222','-o','BatchMode=yes','-o','UserKnownHostsFile='+root+'/private/known_hosts','demo@127.0.0.1'];
const prefix = 'sudo k3s kubectl -n tmp-proof exec -i agent-tmp-proof -- ';
const key='sk-ant-api03-'+'x'.repeat(80);
const setup=`const fs=require('fs');fs.writeFileSync('/home/agent/.claude.json',JSON.stringify({hasCompletedOnboarding:true,theme:'dark',customApiKeyResponses:{approved:['${key.slice(-20)}'],rejected:[]},projects:{'/home/agent':{hasTrustDialogAccepted:true,hasCompletedProjectOnboarding:true}}}));`;
execFileSync('ssh',[...ssh,prefix+'node'],{input:setup});
const term=new Terminal({cols:120,rows:40,allowProposedApi:true});
const command=prefix.replace('exec -i ','exec -it ')+`sh -c 'cd /home/agent && exec env HOME=/home/agent TERM=xterm-256color ANTHROPIC_API_KEY=${key} ANTHROPIC_BASE_URL=http://127.0.0.1:1 CLAUDE_CODE_DISABLE_NONESSENTIAL_TRAFFIC=1 DISABLE_AUTOUPDATER=1 /home/agent/claude.exe'`;
const child=pty.spawn('ssh',['-tt',...ssh,command],{cols:120,rows:40,name:'xterm-256color',env:{PATH:process.env.PATH,HOME:process.env.HOME,TERM:'xterm-256color'}});
let pending=Promise.resolve(), raw='';
child.onExit(event=>console.log('SSH proof transport exited:',event.exitCode));
term.onData(data=>child.write(data));
child.onData(data=>{raw+=data;pending=pending.then(()=>new Promise(resolve=>term.write(data,resolve)));});
const sleep=ms=>new Promise(resolve=>setTimeout(resolve,ms));
(async()=>{
 try {
  let screen='';
  for(let n=0;n<90;n++) {
   await sleep(1000);await pending;
   const b=term.buffer.active;
   screen=Array.from({length:term.rows},(_,i)=>b.getLine(b.baseY+i)?.translateToString(true)||'').join('\n');
   if(screen.includes('auto mode on'))break;
  }
  assert.ok(screen.includes('Claude Code v2.1.287')&&screen.includes('auto mode on'),'CLI did not reach real startup: '+screen);
  assert.ok(!screen.includes('Cross-session messaging is off'),'messaging warning remains');
  const sockets=execFileSync('ssh',[...ssh,prefix+"find /tmp -type s"],{encoding:'utf8'}).trim();
  assert.ok(sockets,'real CLI did not create a messaging socket');
  console.log('Real Claude Code 2.1.287 ready; messaging warning absent; socket created:',sockets);
  fs.writeFileSync(path.join(evidence,'screen.txt'),screen+'\n',{mode:0o600,flag:'wx'});
  const probe = `const fs=require('fs'),net=require('net');const candidates=${JSON.stringify(sockets.split('\n'))};candidates.sort((a,b)=>fs.statSync(b).mtimeMs-fs.statSync(a).mtimeMs);const socket=net.createConnection(candidates[0]);socket.setTimeout(3000);socket.on('connect',()=>{console.log('Native messaging socket accepts connections');socket.end();});socket.on('error',()=>{process.exitCode=1;});socket.on('timeout',()=>{socket.destroy();process.exitCode=1;});`;
  const result=execFileSync('ssh',[...ssh,prefix+'node'],{input:probe,encoding:'utf8'});
  assert.ok(result.includes('Native messaging socket accepts connections'));
  console.log(result.trim());
 } finally {fs.writeFileSync(path.join(evidence,'raw.txt'),raw,{mode:0o600,flag:'wx'});child.kill();term.dispose();}
})().catch(error=>{console.error(error);process.exitCode=1;});
