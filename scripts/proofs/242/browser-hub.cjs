// The real branch runtime and real CLIs, behind a deliberately small stand-in hub.
// Neither PTY frames nor authorization values are logged or saved by this harness.
const fs = require('node:fs');
const path = require('node:path');
const os = require('node:os');
const http = require('node:http');
const { spawn } = require('node:child_process');
const { WebSocket, WebSocketServer } = require('/opt/session-agent/node_modules/ws');
const repo = path.resolve(__dirname, '../../..');
const root = process.env.SIGNIN_PROOF_ROOT || fs.mkdtempSync(path.join(os.tmpdir(), 'signin-browser-'));
const bins = process.env.SIGNIN_CLI_BIN || '/tmp/signin-242-clis/node_modules/.bin';
const cursor = process.env.SIGNIN_CURSOR_BIN || '/tmp/signin-242-cursor/.local/bin/agent';
if (process.argv[2] === '--runtime') {
 const name = process.argv[3];
 const home = fs.mkdtempSync(path.join(root, name + '-'));
 const env = Object.fromEntries(Object.entries(process.env).filter(([key]) => !/^(CLAUDE|AGENTHUB|ANTHROPIC|OPENAI|CODEX|OPENCODE|CURSOR|OPENCLAW|XDG_|.*TOKEN|.*KEY|.*SECRET)/.test(key)));
 Object.assign(env, {HOME:home, XDG_CONFIG_HOME:home+'/.config', XDG_DATA_HOME:home+'/.local/share', XDG_STATE_HOME:home+'/.local/state', PATH:bins+':'+process.env.PATH,
  AGENTHUB_PORT:'7685', AGENTHUB_WORKDIR:home, AGENTHUB_SESSION_ID:'signin-proof', AGENTHUB_CALLBACK_URL:'http://127.0.0.1:8080/internal', AGENTHUB_CALLBACK_TOKEN:'example-proof-token', AGENTHUB_SKILLS_SYNC_SECONDS:'0', AGENTHUB_WATCHER_PIDFILE:path.join(root,'absent-watcher.pid'),
  AGENTHUB_SCROLLBACK_PUT_URL:'http://127.0.0.1:8080/upload/scrollback', AGENTHUB_TRANSCRIPT_PUT_URL:'http://127.0.0.1:8080/upload/transcript', DISABLE_AUTOUPDATER:'1', CLAUDE_CODE_DISABLE_NONESSENTIAL_TRAFFIC:'1', AGENT_CLI_CREDENTIAL_STORE:'file' });
 fs.writeFileSync(home+'/.claude.json', JSON.stringify({hasCompletedOnboarding:true,theme:'dark',projects:{[home]:{hasTrustDialogAccepted:true,hasCompletedProjectOnboarding:true}}}));
 const commands={claude:[path.join(bins,'claude'),[]],codex:[path.join(bins,'codex'),['login','--device-auth']],cursor:[cursor,['login']],openclaw:[path.join(bins,'openclaw'),['models','auth','add']],opencode:[path.join(bins,'opencode'),['auth','login']]};
 const driver=require(path.join(root,'agent-runtime',name,'driver.js'));
 const [cmd,args]=commands[name];
 require(path.join(root,'agent-runtime/common/server.js')).createCommonServer({env,driver:{...driver,buildCommand:()=>({cmd,args}),isResumeCommand:()=>false}});
 process.send?.({ready:true});
 return;
}
fs.cpSync(path.join(repo,'agent-runtime'),path.join(root,'agent-runtime'),{recursive:true,filter:source=>!source.includes('/node_modules')});
fs.symlinkSync('/opt/session-agent/node_modules',path.join(root,'agent-runtime/node_modules'));
const serverPath=path.join(root,'agent-runtime/common/server.js');
let source=fs.readFileSync(serverPath,'utf8');
for(const file of ['state.tgz','scrollback.log','agenthub-auth-watcher.pid']) source=source.split('/tmp/'+file).join(path.join(root,file));
fs.writeFileSync(serverPath,source);
let agent='claude', child, uploads=[];
const session=()=>({id:'signin-proof',title:'CLI sign-in proof',phase:'Running',agent:({claude:'Claude',codex:'Codex',cursor:'Cursor',openclaw:'OpenClaw',opencode:'OpenCode'})[agent],mode:'Interactive',uiMode:'terminal',accessRole:'Owner',authMode:'Subscription',createdAt:new Date().toISOString(),browser:{phase:'Stopped'}});
async function start(name) {
 if(child) {child.kill('SIGTERM');await new Promise(resolve=>child.once('exit',resolve));}
 agent=name;uploads=[];
 child=spawn(process.execPath,[__filename,'--runtime',name],{env:{...process.env,SIGNIN_PROOF_ROOT:root},stdio:['ignore','ignore','ignore','ipc']});
 await new Promise((resolve,reject)=>{child.once('message',resolve);child.once('exit',code=>reject(new Error('Runtime exited '+code)));});
 await new Promise(resolve=>setTimeout(resolve,500));
}
const server=http.createServer(async(req,res)=>{
 const pathname=new URL(req.url,'http://example.com').pathname;
 let data;
 if(pathname==='/__proof/agent' && req.method==='POST') {let body='';for await(const chunk of req)body+=chunk;const name=JSON.parse(body).agent;if(!['claude','codex','cursor','openclaw','opencode'].includes(name)){res.writeHead(400);return res.end();}await start(name);data={ready:true};}
 else if(pathname.startsWith('/internal') || pathname.startsWith('/upload/')) {
  let body='';for await(const chunk of req)body+=chunk;
  if(req.method==='PUT')uploads.push({path:pathname,body});
  data={};
 }
 else if(pathname==='/__proof/check') {
  // Inspect persisted bytes in memory; only boolean results leave the harness.
  const transcript=await fetch('http://127.0.0.1:7685/agenthub/transcript',{headers:{'X-Agent-Token':'example-proof-token'}}).then(r=>r.text());
  data={uploads:uploads.length,authTextInUploads:uploads.some(x=>/oauth\/authorize|code_challenge=|Enter this one-time code|example-authorization-code/.test(x.body)),authTextInTranscript:/oauth\/authorize|code_challenge=|example-authorization-code/.test(transcript)};
 }
 else if(pathname==='/api/config')data={authority:'',version:'proof'};
 else if(pathname==='/api/sessions')data=[session()];
 else if(pathname==='/api/agents/allowed')data={agents:['Claude','Codex','Cursor','OpenClaw','OpenCode']};
 else if(pathname==='/api/admin/access')data={isAdmin:false};
 else if(pathname==='/api/credentials/accounts'||pathname==='/api/credentials')data={};
 else if(pathname.endsWith('/conversation'))data={source:'native',entries:[],text:'',offset:0,nextOffset:0,length:0};
 else if(pathname.endsWith('/files/capabilities'))data={canRead:true,canUpload:false,limits:{presentationPollMilliseconds:30000}};
 else if(pathname.endsWith('/files/presentation'))data={revision:0,fileId:null};
 else if(pathname.endsWith('/commands')){const r=await fetch('http://127.0.0.1:7685/agenthub/commands',{headers:{'X-Agent-Token':'example-proof-token'}});data=await r.json();}
 else if(['/api/projects','/api/mcp-servers'].includes(pathname)||/\/(permissions|messages|files)$/.test(pathname))data=[];
 else {res.writeHead(404);return res.end();}
 res.writeHead(200,{'Content-Type':'application/json'});res.end(JSON.stringify(data));
});
const wss=new WebSocketServer({server});
wss.on('connection',(socket,req)=>{
 if(req.url.endsWith('/events'))return;
 const upstream=new WebSocket('ws://127.0.0.1:7685/','tty');
 const pending=[];
 socket.on('message',data=>{if(upstream.readyState===WebSocket.OPEN)upstream.send(data);else pending.push(data);});
 upstream.on('open',()=>{for(const data of pending)upstream.send(data);});
 upstream.on('message',data=>{if(socket.readyState===WebSocket.OPEN)socket.send(data.toString());});
 upstream.on('close',()=>socket.close());upstream.on('error',()=>socket.close());socket.on('close',()=>upstream.close());
});
server.listen(8080,'0.0.0.0',async()=>{await start(agent);console.log('Real CLI proof hub ready on 8080');});
process.on('SIGTERM',()=>{
 if(child){child.once('exit',()=>{fs.rmSync(root,{recursive:true,force:true});server.close();});child.kill('SIGTERM');}
 else {fs.rmSync(root,{recursive:true,force:true});server.close();}
});
