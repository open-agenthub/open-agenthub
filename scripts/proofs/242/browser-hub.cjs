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
 fs.mkdirSync(home+'/tmp',{mode:0o700});
 const env = Object.fromEntries(Object.entries(process.env).filter(([key]) => !/^(CLAUDE|AGENTHUB|ANTHROPIC|OPENAI|CODEX|OPENCODE|CURSOR|OPENCLAW|XDG_|.*TOKEN|.*KEY|.*SECRET)/.test(key)));
 Object.assign(env, {HOME:home,TMPDIR:home+'/tmp', XDG_CONFIG_HOME:home+'/.config', XDG_DATA_HOME:home+'/.local/share', XDG_STATE_HOME:home+'/.local/state', PATH:bins+':'+process.env.PATH,
  AGENTHUB_PORT:'7685', AGENTHUB_WORKDIR:home, AGENTHUB_SESSION_ID:'signin-proof', AGENTHUB_CALLBACK_URL:'http://127.0.0.1:8080/internal', AGENTHUB_CALLBACK_TOKEN:'example-proof-token', AGENTHUB_SKILLS_SYNC_SECONDS:'0', AGENTHUB_WATCHER_PIDFILE:path.join(root,'absent-watcher.pid'),
  AGENTHUB_STATE_PUT_URL:'http://127.0.0.1:8080/upload/state', AGENTHUB_SCROLLBACK_PUT_URL:'http://127.0.0.1:8080/upload/scrollback', AGENTHUB_TRANSCRIPT_PUT_URL:'http://127.0.0.1:8080/upload/transcript', DISABLE_AUTOUPDATER:'1', CLAUDE_CODE_DISABLE_NONESSENTIAL_TRAFFIC:'1', AGENT_CLI_CREDENTIAL_STORE:'file' });
 fs.writeFileSync(home+'/.claude.json', JSON.stringify({hasCompletedOnboarding:true,theme:'dark',projects:{[home]:{hasTrustDialogAccepted:true,hasCompletedProjectOnboarding:true}}}));
 if(process.env.SIGNIN_MOCK_AUTH==='1') {
  env.HTTPS_PROXY='http://127.0.0.1:18443';
  env.NODE_EXTRA_CA_CERTS=process.env.SIGNIN_TLS_CA;
  if(!env.NODE_EXTRA_CA_CERTS)throw new Error('SIGNIN_TLS_CA is required for the fake identity proof');
  fs.mkdirSync(home+'/.claude',{recursive:true});
  fs.writeFileSync(home+'/.claude/.credentials.json',JSON.stringify({claudeAiOauth:{accessToken:'example-expired-access',refreshToken:'example-expired-refresh',expiresAt:Date.now()-1000,scopes:['user:inference','user:profile','user:sessions:claude_code'],subscriptionType:'max',rateLimitTier:'default_claude_max_20x'}}));
  const config=JSON.parse(fs.readFileSync(home+'/.claude.json','utf8'));
  config.oauthAccount={accountUuid:'example-account',emailAddress:'person@example.com',organizationUuid:'example-organization',organizationName:'Example'};
  fs.writeFileSync(home+'/.claude.json',JSON.stringify(config));
 }
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
source=source.replace('signInPrivacy.releaseAbsent(sources, [], fs);', 'const released = signInPrivacy.releaseAbsent(sources, [], fs); process.send?.({privacy:{released,retained:signInPrivacy.retained()}});');
fs.writeFileSync(serverPath,source);
let agent='claude', child, uploads=[], watchTerminal, authTokens=new Set(), authMode=null, privacy=null;
function rememberAuth(value){if(value && value.length>=4)authTokens.add(value)}
const session=()=>({id:'signin-proof',title:'CLI sign-in proof',phase:'Running',agent:({claude:'Claude',codex:'Codex',cursor:'Cursor',openclaw:'OpenClaw',opencode:'OpenCode'})[agent],mode:'Interactive',uiMode:'terminal',accessRole:'Owner',authMode:'Subscription',createdAt:new Date().toISOString(),browser:{phase:'Stopped'}});
async function start(name, mock = false) {
 if(child) {child.kill('SIGTERM');await new Promise(resolve=>child.once('exit',resolve));}
 agent=name;uploads=[];authTokens=new Set();authMode=null;privacy=null;
 watchTerminal?.dispose();
 watchTerminal=new (require('/opt/session-agent/node_modules/@xterm/headless').Terminal)({cols:120,rows:32,allowProposedApi:true,scrollback:0});
 child=spawn(process.execPath,[__filename,'--runtime',name],{env:{...process.env,SIGNIN_PROOF_ROOT:root,SIGNIN_MOCK_AUTH:mock?'1':'0'},stdio:['ignore','ignore','ignore','ipc']});
 child.on('message',message=>{if(message.privacy)privacy=message.privacy;});
 await new Promise((resolve,reject)=>{child.once('message',resolve);child.once('exit',code=>reject(new Error('Runtime exited '+code)));});
 await new Promise(resolve=>setTimeout(resolve,500));
}
const server=http.createServer(async(req,res)=>{
 const pathname=new URL(req.url,'http://example.com').pathname;
 let data;
 if(pathname==='/__proof/agent' && req.method==='POST') {let body='';for await(const chunk of req)body+=chunk;const {agent:name,mock}=JSON.parse(body);if(!['claude','codex','cursor','openclaw','opencode'].includes(name)){res.writeHead(400);return res.end();}await start(name,mock);data={ready:true};}
 else if(pathname.startsWith('/internal') || pathname.startsWith('/upload/')) {
  const chunks=[];for await(const chunk of req)chunks.push(chunk);
  if(req.method==='PUT')uploads.push({path:pathname,body:Buffer.concat(chunks)});
  data={};
 }
 else if(pathname==='/__proof/check') {
  // Inspect persisted bytes in memory; only boolean results leave the harness.
  const transcript=await fetch('http://127.0.0.1:7685/agenthub/transcript',{headers:{'X-Agent-Token':'example-proof-token'}}).then(r=>r.text());
  const payloads=uploads.map(x=>x.path.endsWith('/state')?require('node:zlib').gunzipSync(x.body).toString('utf8'):x.body.toString('utf8'));
  const sensitive=text=>/oauth\/authorize|code_challenge=|Enter this one-time code|example-authorization-code/.test(text)||[...authTokens].some(value=>text.includes(value));
  data={privacy,uploads:uploads.length,stateUploads:uploads.filter(x=>x.path.endsWith('/state')).length,authTextInUploads:payloads.some(sensitive),authTextInTranscript:sensitive(transcript)};
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
 socket.on('message',data=>{
  try{const message=JSON.parse(data.toString());if(message.type==='resize')watchTerminal.resize(message.cols,message.rows);
   if(message.type==='input' && ['code','credential'].includes(authMode) && message.data.startsWith('\x1b[200~')){const value=message.data.replace(/\x1b\[20[01]~/g,'');if(!value.startsWith('/'))rememberAuth(value);}
  }catch{}
  if(upstream.readyState===WebSocket.OPEN)upstream.send(data);else pending.push(data);});
 upstream.on('open',()=>{for(const data of pending)upstream.send(data);});
 upstream.on('message',data=>{
  let control=false;try{control=JSON.parse(data.toString()).type==='agenthub'}catch{}
  if(!control)watchTerminal.write(data.toString(),()=>{
   const buffer=watchTerminal.buffer.active;
   const rows=Array.from({length:watchTerminal.rows},(_,y)=>buffer.getLine(buffer.baseY+y)?.translateToString(true).trim()||'');
   const signIn=require(path.join(repo,'agent-runtime/common/cli-sign-in')).detectSignIn(rows);authMode=signIn?.mode;
   if(signIn?.code)rememberAuth(signIn.code);if(signIn?.url)rememberAuth(signIn.url);
   if(signIn){let continuation=false;for(const row of rows){const match=/https?:\/\/\S+/.exec(row);if(match){rememberAuth(match[0]);continuation=true;}else if(continuation && /^\S+$/.test(row)){const publicRoute=/^(login|authorize|oauth|device|auth)$/i.test(row);if(!publicRoute)rememberAuth(row);}else continuation=false;}}
  });
  if(socket.readyState===WebSocket.OPEN)socket.send(data.toString());});
 upstream.on('close',()=>socket.close());upstream.on('error',()=>socket.close());socket.on('close',()=>upstream.close());
});
server.listen(8080,'0.0.0.0',async()=>{await start(agent);console.log('Real CLI proof hub ready on 8080');});
process.on('SIGTERM',()=>{
 if(child){child.once('exit',()=>{fs.rmSync(root,{recursive:true,force:true});server.close();});child.kill('SIGTERM');}
 else {fs.rmSync(root,{recursive:true,force:true});server.close();}
});
