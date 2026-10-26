// Observe actual login wrappers and periodic persistence; terminal/auth bytes stay in memory.
const fs = require('node:fs');
const path = require('node:path');
const os = require('node:os');
const http = require('node:http');
const { spawn, execFileSync } = require('node:child_process');
const { WebSocket } = require('/opt/session-agent/node_modules/ws');
const { Terminal } = require('/opt/session-agent/node_modules/@xterm/headless');
const repo = path.resolve(__dirname, '../../..');
const name = process.argv[2];
if (!['codex', 'opencode'].includes(name)) throw new Error('Choose a supported login lifecycle');
if (process.argv[3] === '--runtime') {
 const home = process.env.SIGNIN_PROBE_HOME, port = process.env.SIGNIN_PROBE_PORT;
 const env = { HOME:home, PATH:(process.env.SIGNIN_CLI_BIN||'/tmp/signin-242-clis/node_modules/.bin')+':/usr/bin:/bin:/usr/local/bin', TMPDIR:home+'/tmp', TERM:'xterm-256color', XDG_CONFIG_HOME:home+'/.config', XDG_DATA_HOME:home+'/.local/share', XDG_STATE_HOME:home+'/.local/state', AGENTHUB_MODE:'interactive', AGENTHUB_WORKDIR:home+'/work', AGENTHUB_STATE_RESTORED:'0', AGENTHUB_SESSION_ID:'private-lifecycle', AGENTHUB_CALLBACK_TOKEN:'example-probe-token', AGENTHUB_CALLBACK_URL:'http://127.0.0.1:'+port+'/internal', AGENTHUB_STATE_PUT_URL:'http://127.0.0.1:'+port+'/state', AGENTHUB_SCROLLBACK_PUT_URL:'http://127.0.0.1:'+port+'/scrollback', AGENTHUB_TRANSCRIPT_PUT_URL:'http://127.0.0.1:'+port+'/transcript', AGENTHUB_PORT:'7787', AGENTHUB_FILE_ROOT:home+'/files', AGENTHUB_SKILLS_SYNC_SECONDS:'0', AGENTHUB_WATCHER_PIDFILE:home+'/absent.pid', DISABLE_AUTOUPDATER:'1', OPENCODE_DISABLE_AUTOUPDATE:'1', OPENCODE_DISABLE_SHARE:'1' };
 fs.mkdirSync(home+'/work');fs.mkdirSync(home+'/tmp');execFileSync('git',['init','-q',home+'/work']);
 if(name==='codex') {
  if(!process.env.SIGNIN_TLS_CA)throw new Error('Local test CA is required');
  Object.assign(env,{CODEX_HOME:home+'/.codex',AGENTHUB_CODEX_DEVICE_AUTH:'1',HTTPS_PROXY:'http://127.0.0.1:18443',SSL_CERT_FILE:process.env.SIGNIN_TLS_CA,NODE_EXTRA_CA_CERTS:process.env.SIGNIN_TLS_CA});
  fs.mkdirSync(env.CODEX_HOME);
  fs.writeFileSync(env.CODEX_HOME+'/config.toml','check_for_update_on_startup = false\n[projects.'+JSON.stringify(home+'/work')+']\ntrust_level = "trusted"\n');
 } else {
  fs.mkdirSync(home+'/.opencode');fs.mkdirSync(home+'/.local/share',{recursive:true});fs.symlinkSync(home+'/.opencode',home+'/.local/share/opencode');
  Object.assign(env,{OPENCODE_AUTH_FILE:home+'/.opencode/auth.json',AGENTHUB_OPENCODE_LOGIN:'1'});
 }
 const runtime = require(path.join(repo,'agent-runtime/common/server')).createCommonServer({env,driver:require(path.join(repo,'agent-runtime',name,'driver'))});
 runtime.httpServer.on('listening',()=>process.send?.({ready:true}));
 return;
}
const wait = ms => new Promise(resolve=>setTimeout(resolve,ms));
(async()=>{
 const home = fs.mkdtempSync(path.join(os.tmpdir(),'private-login-'+name+'-'));
 const values = new Set(), uploads=[];
 let child, socket, sawAuth=false, sawReady=false, withheld=false, selected=false, submitted=false, screenFacts=null;
 const terminal = new Terminal({cols:120,rows:40,allowProposedApi:true,scrollback:0});
 const server = http.createServer(async(req,res)=>{const chunks=[];for await(const chunk of req)chunks.push(chunk);if(req.method==='PUT')uploads.push({route:req.url,body:Buffer.concat(chunks)});res.writeHead(200,{'Content-Type':'application/json'});res.end('{}');});
 try {
  await new Promise(resolve=>server.listen(0,'127.0.0.1',resolve));
  child=spawn(process.execPath,[__filename,name,'--runtime'],{env:{...process.env,NODE_PATH:'/opt/session-agent/node_modules',SIGNIN_PROBE_HOME:home,SIGNIN_PROBE_PORT:String(server.address().port)},stdio:['ignore','ignore','pipe','ipc']});
  child.stderr.on('data',data=>{const line=data.toString().split('\n').find(line=>/^Error:/.test(line));if(line)console.error(line.replace(/https?:\/\/\S+/g,'[URL omitted]'));});
  await new Promise((resolve,reject)=>{child.once('message',resolve);child.once('exit',()=>reject(new Error('Runtime exited before listening')));});
  socket=new WebSocket('ws://127.0.0.1:7787/','tty');
  await new Promise((resolve,reject)=>{socket.once('open',resolve);socket.once('error',reject);});
  const send=data=>socket.send(JSON.stringify({type:'input',data}));
  terminal.onData(send);
  socket.send(JSON.stringify({type:'resize',cols:120,rows:40}));
  socket.send(JSON.stringify({type:'subscribe',channel:'activity'}));
  socket.on('message',data=>{
   let control;try{control=JSON.parse(data.toString());}catch{}
   if(control?.type==='agenthub'){if(control.subtype==='persistence')withheld=control.withheld;return;}
   terminal.write(data.toString(),()=>{
    const b=terminal.buffer.active,rows=Array.from({length:terminal.rows},(_,i)=>b.getLine(b.baseY+i)?.translateToString(true)||'');
    const text=rows.join('\n'), signIn=require(path.join(repo,'agent-runtime/common/cli-sign-in')).detectSignIn(rows);
    screenFacts={signInStatus:signIn?.status||null,privacyPrompt:rows.filter(row=>row.trim()).slice(-10).some(row=>/^\s*[❯›>]\s*(?:Ask (?:Codex|anything).*|)$/.test(row)),nativeReady:require(path.join(repo,'agent-runtime',name,'driver')).contextClearScreen?.(rows)||false};
    if(signIn){sawAuth=true;if(signIn.url)values.add(signIn.url);if(signIn.code)values.add(signIn.code);}
    if(name==='opencode'&&!selected&&text.includes('Select provider')){selected=true;send('\r');}
    if(name==='opencode'&&!submitted&&signIn?.mode==='credential'){
     submitted=true;const credential='example-263-test-credential';values.add(credential);
     send('\x1b[200~'+credential+'\x1b[201~');setTimeout(()=>send('\r'),150);
    }
    if(name==='codex'&&/>_ .*Codex/.test(text)&&rows.some(r=>/^\s*›/.test(r)))sawReady=true;
    if(name==='opencode'&&rows.some(r=>/┃\s+Ask anything/.test(r))&&/tab\s+agents\s+ctrl\+p\s+commands/.test(text))sawReady=true;
   });
  });
  for(let i=0;i<200&&!sawReady;i++)await wait(100);
  await wait(33000);
  const hits=body=>[...values].some(value=>body.includes(value));
  const archives=uploads.filter(u=>u.route==='/state');
  let credentialHits=0,nativeExportHits=0;
  const driver=require(path.join(repo,'agent-runtime',name,'driver'));
  const excludes=require(path.join(repo,'agent-runtime/common/state-archive')).stateExcludes(driver);
  function visit(file) {
   let fd;
   try {
    // Inspect the opened inode so a replaced path cannot redirect the private audit.
    fd=fs.openSync(file,fs.constants.O_RDONLY|fs.constants.O_NOFOLLOW|fs.constants.O_NONBLOCK);
    const before=fs.fstatSync(fd);
    if(before.isDirectory()){fs.closeSync(fd);fd=undefined;for(const n of fs.readdirSync(file))visit(path.join(file,n));return;}
    if(!before.isFile())return;
    const content=fs.readFileSync(fd).toString('utf8'),after=fs.fstatSync(fd),named=fs.lstatSync(file);
    if(named.dev!==before.dev||named.ino!==before.ino||after.size!==before.size||after.mtimeMs!==before.mtimeMs)throw Error('Native state changed during private audit');
    const relative=path.relative(home,file);
    const excluded=excludes.some(pattern=>new RegExp('^'+pattern.split('*').map(p=>p.replaceAll('.','[.]')).join('.*')+'(?:/|$)').test(relative));
    if(hits(content)){if(excluded)credentialHits++;else nativeExportHits++;}
   } catch(error){if(error.code!=='ELOOP')throw error;}
   finally{if(fd!==undefined)fs.closeSync(fd);}
  }
  visit(path.join(home,driver.stateDir));
  console.log(JSON.stringify({agent:name,sawAuth,sawReady,withheld,screenFacts,observedValues:values.size,stateUploads:archives.length,authInArchive:archives.some(u=>hits(require('node:zlib').gunzipSync(u.body).toString('utf8'))),authInTextExports:uploads.filter(u=>u.route!=='/state').some(u=>hits(u.body.toString('utf8'))),credentialFileHits:credentialHits,nativeExportFileHits:nativeExportHits}));
 } finally {
  socket?.close();
  if(child&&child.exitCode===null){const ended=new Promise(resolve=>child.once('exit',resolve));child.kill('SIGTERM');await ended;}
  terminal.dispose();await new Promise(resolve=>server.close(resolve));fs.rmSync(home,{recursive:true,force:true});
 }
})().catch(error=>{console.error(error.message);process.exitCode=1;});
