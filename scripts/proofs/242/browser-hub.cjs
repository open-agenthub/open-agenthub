// The real branch runtime and real CLIs, behind a deliberately small stand-in hub.
// Neither PTY frames nor authorization values are logged or saved by this harness.
const fs = require('node:fs');
// Select the candidate dependency tree without mutating the installed baseline modules.
const modules = process.env.SIGNIN_NODE_MODULES || '/opt/session-agent/node_modules';
const path = require('node:path');
const os = require('node:os');
const http = require('node:http');
const { spawn } = require('node:child_process');
const { WebSocket, WebSocketServer } = require(modules + '/ws');
const repo = path.resolve(__dirname, '../../..');
const root = process.env.SIGNIN_PROOF_ROOT || fs.mkdtempSync(path.join(os.tmpdir(), 'signin-browser-'));
const bins = process.env.SIGNIN_CLI_BIN || '/tmp/signin-242-clis/node_modules/.bin';
const cursor = process.env.SIGNIN_CURSOR_BIN || '/tmp/signin-242-cursor/.local/bin/agent';
if (process.argv[2] === '--runtime') {
 const name = process.argv[3];
 const home = fs.mkdtempSync(path.join(root, name + '-'));
 fs.mkdirSync(home+'/tmp',{mode:0o700});
 const env = process.env.SIGNIN_LIFECYCLE==='1'?{}:Object.fromEntries(Object.entries(process.env).filter(([key]) => !/^(CLAUDE|AGENTHUB|ANTHROPIC|OPENAI|CODEX|OPENCODE|CURSOR|OPENCLAW|XDG_|.*TOKEN|.*KEY|.*SECRET)/.test(key)));
 Object.assign(env, {HOME:home,TMPDIR:home+'/tmp',TERM:'xterm-256color',OPENCODE_DISABLE_AUTOUPDATE:'1',OPENCODE_DISABLE_SHARE:'1', XDG_CONFIG_HOME:home+'/.config', XDG_DATA_HOME:home+'/.local/share', XDG_STATE_HOME:home+'/.local/state', PATH:bins+':'+process.env.PATH,
  AGENTHUB_STATE_RESTORED:'0', AGENTHUB_FILE_ROOT:home+'/files', AGENTHUB_PORT:'7685', AGENTHUB_WORKDIR:home, AGENTHUB_SESSION_ID:'signin-proof', AGENTHUB_CALLBACK_URL:'http://127.0.0.1:8080/internal', AGENTHUB_CALLBACK_TOKEN:'example-proof-token', AGENTHUB_SKILLS_SYNC_SECONDS:'0', AGENTHUB_WATCHER_PIDFILE:path.join(root,'absent-watcher.pid'),
  AGENTHUB_STATE_PUT_URL:'http://127.0.0.1:8080/upload/state', AGENTHUB_SCROLLBACK_PUT_URL:'http://127.0.0.1:8080/upload/scrollback', AGENTHUB_TRANSCRIPT_PUT_URL:'http://127.0.0.1:8080/upload/transcript', DISABLE_AUTOUPDATER:'1', CLAUDE_CODE_DISABLE_NONESSENTIAL_TRAFFIC:'1', AGENT_CLI_CREDENTIAL_STORE:'file' });
 fs.writeFileSync(home+'/.claude.json', JSON.stringify({hasCompletedOnboarding:true,theme:'dark',projects:{[home]:{hasTrustDialogAccepted:true,hasCompletedProjectOnboarding:true}}}));
 if(process.env.SIGNIN_MOCK_AUTH==='1'&&['claude','codex'].includes(name)) {
  env.HTTPS_PROXY='http://127.0.0.1:18443';
  env.NODE_EXTRA_CA_CERTS=process.env.SIGNIN_TLS_CA;
  env.SSL_CERT_FILE=process.env.SIGNIN_TLS_CA;
  if(!env.NODE_EXTRA_CA_CERTS)throw new Error('SIGNIN_TLS_CA is required for the fake identity proof');
  if(name==='claude') {
  fs.mkdirSync(home+'/.claude',{recursive:true});
  fs.writeFileSync(home+'/.claude/.credentials.json',JSON.stringify({claudeAiOauth:{accessToken:'example-expired-access',refreshToken:'example-expired-refresh',expiresAt:Date.now()-1000,scopes:['user:inference','user:profile','user:sessions:claude_code'],subscriptionType:'max',rateLimitTier:'default_claude_max_20x'}}));
  const config=JSON.parse(fs.readFileSync(home+'/.claude.json','utf8'));
  config.oauthAccount={accountUuid:'example-account',emailAddress:'person@example.com',organizationUuid:'example-organization',organizationName:'Example'};
  fs.writeFileSync(home+'/.claude.json',JSON.stringify(config));
  }
 }
 if(process.env.SIGNIN_QUESTION_MODEL && name==='claude') {
  const key='sk-ant-api03-'+'x'.repeat(80);
  Object.assign(env,{ANTHROPIC_API_KEY:key,ANTHROPIC_BASE_URL:process.env.SIGNIN_QUESTION_MODEL});
  const config=JSON.parse(fs.readFileSync(home+'/.claude.json','utf8'));
  config.customApiKeyResponses={approved:[key.slice(-20)],rejected:[]};
  fs.writeFileSync(home+'/.claude.json',JSON.stringify(config));
 }
 const commands={claude:[path.join(bins,'claude'),[]],codex:[path.join(bins,'codex'),['login','--device-auth']],cursor:[cursor,['login']],openclaw:[path.join(bins,'openclaw'),['models','auth','add']],opencode:[path.join(bins,'opencode'),['auth','login']]};
 const driver=require(path.join(root,'agent-runtime',name,'driver.js'));
 if(process.env.SIGNIN_LIFECYCLE==='1'&&['codex','opencode'].includes(name)) {
  // Exercise the interactive wrapper after login instead of stopping at its login subprocess.
  env.TERMINATION_MESSAGE_PATH=home+'/termination.txt';
  if(name==='codex') {
   env.CODEX_HOME=home+'/.codex';env.AGENTHUB_CODEX_DEVICE_AUTH='1';
   fs.mkdirSync(env.CODEX_HOME,{recursive:true});
   fs.writeFileSync(env.CODEX_HOME+'/config.toml','check_for_update_on_startup = false\n[projects.'+JSON.stringify(home)+']\ntrust_level = "trusted"\n');
  } else {
   fs.mkdirSync(home+'/.opencode');fs.mkdirSync(home+'/.local/share',{recursive:true});
   fs.symlinkSync(home+'/.opencode',home+'/.local/share/opencode');
   env.OPENCODE_AUTH_FILE=home+'/.opencode/auth.json';env.AGENTHUB_OPENCODE_LOGIN='1';
  }
  const runtime=require(path.join(root,'agent-runtime/common/server.js')).createCommonServer({env,driver});
  runtime.httpServer.on('listening',()=>process.send?.({ready:true}));return;
 }
 const [cmd,args]=commands[name];
 require(path.join(root,'agent-runtime/common/server.js')).createCommonServer({env,driver:{...driver,buildCommand:()=>({cmd,args}),isResumeCommand:()=>false}});
 process.send?.({ready:true});
 return;
}
if(process.env.SIGNIN_SOURCE_REF){
 const archive=require('node:child_process').execFileSync('git',['archive',process.env.SIGNIN_SOURCE_REF,'agent-runtime'],{cwd:repo,maxBuffer:32*1024*1024});
 require('node:child_process').execFileSync('tar',['-xf','-','-C',root],{input:archive});
}else fs.cpSync(path.join(repo,'agent-runtime'),path.join(root,'agent-runtime'),{recursive:true,filter:source=>!source.includes('/node_modules')});
fs.symlinkSync(modules,path.join(root,'agent-runtime/node_modules'));
const serverPath=path.join(root,'agent-runtime/common/server.js');
let source=fs.readFileSync(serverPath,'utf8');
for(const file of ['state.tgz','scrollback.log','agenthub-auth-watcher.pid']) source=source.split('/tmp/'+file).join(path.join(root,file));
source=source.replace('signInPrivacy.releaseAbsent(sources, [], fs);', 'const released = signInPrivacy.releaseAbsent(sources, [], fs); process.send?.({privacy:{released,retained:signInPrivacy.retained()}});');
fs.writeFileSync(serverPath,source);
let agent=process.env.SIGNIN_LIFECYCLE==='1'?'codex':'claude', child, uploads=[], watchTerminal, authTokens=new Set(), authMode=null, privacy=null, providerSelected=false, methodSelected=false, lifecycleSocket;
function rememberAuth(value){if(value && value.length>=4)authTokens.add(value)}
const session=()=>({id:'signin-proof',title:'CLI sign-in proof',phase:'Running',agent:({claude:'Claude',codex:'Codex',cursor:'Cursor',openclaw:'OpenClaw',opencode:'OpenCode'})[agent],mode:'Interactive',uiMode:'terminal',accessRole:'Owner',authMode:'Subscription',createdAt:new Date().toISOString(),browser:{phase:'Stopped'}});
async function start(name, mock = false) {
 if(child&&child.exitCode===null) {const ended=new Promise(resolve=>child.once('exit',resolve));child.kill('SIGTERM');await ended;}
 agent=name;uploads=[];authTokens=new Set();authMode=null;privacy=null;providerSelected=false;methodSelected=false;
 watchTerminal?.dispose();
 watchTerminal=new (require(modules + '/@xterm/headless').Terminal)({cols:120,rows:32,allowProposedApi:true,scrollback:0});
 child=spawn(process.execPath,[__filename,'--runtime',name],{env:{...process.env,SIGNIN_PROOF_ROOT:root,SIGNIN_MOCK_AUTH:mock?'1':'0'},stdio:['ignore','ignore','pipe','ipc']});
 child.stderr.on('data',bytes=>{const error=bytes.toString().split('\n').find(line=>/^Error:/.test(line));if(error)console.error(error.replace(/https?:\/\/\S+/g,'[URL omitted]'));});
 child.on('exit',code=>console.log('Isolated runtime exit code:',code));
 child.on('message',message=>{if(message.privacy)privacy=message.privacy;});
 await new Promise((resolve,reject)=>{child.once('message',resolve);child.once('exit',code=>reject(new Error('Runtime exited '+code)));});
 await new Promise(resolve=>setTimeout(resolve,500));
 if(process.env.SIGNIN_LIFECYCLE==='1'&&name==='opencode') {
  // A separate terminal observer avoids interleaving duplicate browser subscriptions.
  const terminal=new (require(modules + '/@xterm/headless').Terminal)({cols:120,rows:40,allowProposedApi:true,scrollback:0});
  lifecycleSocket=new WebSocket('ws://127.0.0.1:7685/','tty');
  const send=data=>lifecycleSocket.send(JSON.stringify({type:'input',data}));
  terminal.onData(send);
  lifecycleSocket.on('message',data=>{let control;try{control=JSON.parse(data.toString());}catch{}if(control?.type==='agenthub')return;
   terminal.write(data.toString(),()=>{const b=terminal.buffer.active;const rows=Array.from({length:terminal.rows},(_,i)=>b.getLine(b.baseY+i)?.translateToString(true)||'');
    if(!methodSelected&&rows.some(row=>row.includes('Login method'))&&rows.some(row=>row.includes('Manually enter API Key'))){methodSelected=true;setTimeout(()=>{if(lifecycleSocket.readyState===WebSocket.OPEN){send('\x1b[B');setTimeout(()=>{if(lifecycleSocket.readyState===WebSocket.OPEN)send('\r')},150)}},500);}
    if(!providerSelected&&rows.some(row=>row.includes('Select provider'))){providerSelected=true;setTimeout(()=>{if(lifecycleSocket.readyState===WebSocket.OPEN)send('\r')},750);}
   });
  });
  lifecycleSocket.on('close',()=>terminal.dispose());
 }

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
 const terminalReplies=process.env.SIGNIN_LIFECYCLE==='1'?watchTerminal.onData(data=>{if(upstream.readyState===WebSocket.OPEN)upstream.send(JSON.stringify({type:'input',data}))}):null;
 socket.on('close',()=>terminalReplies?.dispose());
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
   if(signIn){let continuation=false;for(const row of rows){const match=/https?:\/\/\S+/.exec(row);if(match){rememberAuth(match[0]);continuation=true;}else if(continuation && /^\S+$/.test(row)){const publicRoute=row.length<=6&&/^[A-Za-z]+$/.test(row);if(!publicRoute)rememberAuth(row);}else continuation=false;}}
  });
  if(socket.readyState===WebSocket.OPEN)socket.send(data.toString());});
 upstream.on('close',()=>socket.close());upstream.on('error',()=>socket.close());socket.on('close',()=>upstream.close());
});
server.listen(8080,'0.0.0.0',async()=>{await start(agent,process.env.SIGNIN_LIFECYCLE==='1');console.log('Real CLI proof hub ready on 8080');});
process.on('SIGTERM',()=>{
 lifecycleSocket?.close();
 if(child&&child.exitCode===null){child.once('exit',()=>{fs.rmSync(root,{recursive:true,force:true});server.close();});child.kill('SIGTERM');}
 else {fs.rmSync(root,{recursive:true,force:true});server.close();}
});
