// Compare actual runtime startup without retaining terminal frames or credentials.
const fs = require('node:fs');
const os = require('node:os');
const path = require('node:path');
const { spawn, execFileSync } = require('node:child_process');
const { WebSocket } = require('/opt/session-agent/node_modules/ws');
const { Terminal } = require('/opt/session-agent/node_modules/@xterm/headless');
const repo = path.resolve(__dirname, '../../..');
if (process.argv[2] === '--runtime') {
 const root = process.argv[3], home = path.join(root, 'home');
 fs.mkdirSync(home);
 const env = { HOME: home, PATH: process.env.PATH, TMPDIR: home, TERM: 'xterm-256color', XDG_CONFIG_HOME: home+'/.config', XDG_DATA_HOME: home+'/.local/share', XDG_STATE_HOME: home+'/.local/state', AGENTHUB_SESSION_ID:'credential-proof', AGENTHUB_CALLBACK_URL:'http://127.0.0.1:1/internal', AGENTHUB_CALLBACK_TOKEN:'example-proof-token', AGENTHUB_PORT: '7788', AGENTHUB_WORKDIR: home, AGENTHUB_SKILLS_SYNC_SECONDS: '0', AGENTHUB_WATCHER_PIDFILE: home+'/absent.pid' };
 const driver = require(path.join(root, 'agent-runtime/opencode/driver.js'));
 require(path.join(root, 'agent-runtime/common/server.js')).createCommonServer({env, driver:{...driver,buildCommand:()=>({cmd:path.join(process.env.SIGNIN_CLI_BIN || '/tmp/signin-242-clis/node_modules/.bin','opencode'),args:['auth','login']}),isResumeCommand:()=>false}});
 return;
}
(async () => {
 for (const ref of ['origin/main', 'WORKTREE']) {
  const root = fs.mkdtempSync(path.join(os.tmpdir(), 'credential-runtime-'));
  let child, socket;
  const terminal = new Terminal({cols:120,rows:32,allowProposedApi:true,scrollback:0});
  try {
   if(ref==='WORKTREE')fs.cpSync(path.join(repo,'agent-runtime'),path.join(root,'agent-runtime'),{recursive:true,filter:p=>!p.includes('/node_modules')});
   else execFileSync('tar',['-xf','-','-C',root],{input:execFileSync('git',['archive',ref,'agent-runtime'],{cwd:repo,maxBuffer:32*1024*1024})});
   fs.symlinkSync('/opt/session-agent/node_modules',path.join(root,'agent-runtime/node_modules'));
   child=spawn(process.execPath,[__filename,'--runtime',root],{stdio:['ignore','ignore','pipe']});
   child.stderr.on('data',data=>{const line=data.toString().split('\n').find(line=>/^Error:/.test(line));if(line)console.error(line);});
   let selected=false, complete=false;
   for(let attempt=0;attempt<100&&!socket;attempt++) {
    const candidate=new WebSocket('ws://127.0.0.1:7788/','tty');
    const opened=await new Promise(resolve=>{candidate.once('open',()=>resolve(true));candidate.once('error',()=>resolve(false));});
    if(opened)socket=candidate;else{candidate.terminate();await new Promise(resolve=>setTimeout(resolve,100));}
   }
   if(!socket)throw new Error('Runtime did not start');
   terminal.onData(data=>socket.send(JSON.stringify({type:'input',data})));
   socket.on('message',data=>terminal.write(data.toString(),()=>{
    const b=terminal.buffer.active;
    const text=Array.from({length:terminal.rows},(_,i)=>b.getLine(b.baseY+i)?.translateToString(true)||'').join('\n');
    if(!selected&&text.includes('Select provider')){selected=true;socket.send(JSON.stringify({type:'input',data:'\r'}));}
    if(selected&&text.includes('Enter your API key'))complete=true;
   }));
   for(let attempt=0;attempt<150&&!complete;attempt++)await new Promise(resolve=>setTimeout(resolve,100));
   if(!complete)throw new Error('Native credential screen did not appear');
   console.log('PASS '+ref+' real runtime: provider selector -> credential screen');
  } finally {
   socket?.close();
   if(child && child.exitCode===null){const ended=new Promise(resolve=>child.once('exit',resolve));child.kill('SIGTERM');await ended;}
   terminal.dispose();fs.rmSync(root,{recursive:true,force:true});
  }
 }
})().catch(()=>{console.error('Credential runtime comparison failed; terminal contents omitted');process.exitCode=1;});
