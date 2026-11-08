const http = require('node:http');
const { WebSocketServer } = require(require('node:path').join(process.env.AGENTHUB_REPO || process.cwd(),'agent-runtime/session-agent/node_modules/ws'));
const session = {id:'browser-test',title:'Workspace browser test',phase:'Running',agent:'Codex',authMode:'Subscription',credentialId:'acct-1',mode:'Interactive',uiMode:'terminal',expiresAt:new Date(Date.now()+3*3600e3).toISOString(),autoDeleteFrom:'lastActivity',accessRole:'Owner',createdAt:new Date().toISOString(),browser:{phase:'Stopped'}};
const entries = [
 {role:'user',text:'Prüfe die Workspace-Oberfläche mit langen Antworten und Werkzeugausgaben.'},
 {role:'tool',tool:'exec_command',text:'npm run build'},
 {role:'result',text:'Build completed successfully.'},
 ...Array.from({length:12},(_,i)=>({role:'assistant',text:`## Prüfschritt ${i+1}\n\nDie Oberfläche zeigt **formatierte Antworten**, Quelltext und Werkzeugausgaben.\n\n\`\`\`js\nconsole.log("Workspace ${i+1}")\n\`\`\`\n\n- Shell öffnen\n- Nachrichten senden\n- Entwürfe behalten`})),
 {role:'assistant',text:'## Browser-Prüfung\n\nDie lokale UI ist bereit. **Transport und Antwort sind Testdaten**, kein laufender Modellaufruf.\n\n| Funktion | Status |\n| --- | --- |\n| Workspace | bereit |\n| Shell | bereit |'}
];
const sessions=[session,{...session,id:'second-session',title:'Second workspace'},{...session,id:'stream-session',title:'Streaming workspace',agent:'Claude',uiMode:'chat'},
 {...session,id:'opencode-session',title:'OpenCode workspace',agent:'OpenCode',authMode:'ApiKey',credentialId:null,expiresAt:null},
 {...session,id:'browser-on',title:'Session with a browser',browser:{phase:'Running'}},
 {...session,id:'presented',title:'Session with a presented file',browser:{phase:'Running'}},
 {...session,id:'empty-run',title:'Run with nothing saved',mode:'Autonomous',phase:'Succeeded',expiresAt:null},
 {...session,id:'auto-run',title:'Finished nightly run',mode:'Autonomous',phase:'Succeeded',canResume:true,canConvertToInteractive:true,expiresAt:null}];
const accounts={Codex:[{id:'acct-1',label:'Work',email:'firstname.lastname-with-a-long-alias@engineering.your-org.example',isDefault:true},{id:'acct-2',label:'Personal',email:'other@example.com'}]};
const messages=[
 {id:'m1',body:'Priority: the release branch is frozen, rebase onto it.',priority:true,interrupt:false,deliveredVia:'injected',deliveredAt:new Date().toISOString(),fromTitle:'Release coordinator',createdAt:new Date().toISOString()},
 {id:'m2',body:'Stop and check the failing test first.',priority:true,interrupt:true,deliveredVia:'mod',deliveredAt:new Date().toISOString(),fromTitle:'Reviewer',createdAt:new Date().toISOString()},
 {id:'m3',body:'FYI: docs are updated.',priority:false,interrupt:false,deliveredVia:null,deliveredAt:null,fromTitle:null,createdAt:new Date().toISOString()}];
const shared=[{...session,id:'viewer-session',title:'Session shared with me (viewer)',accessRole:'Viewer',sharedBy:'owner@example.com',credentialId:null}];
const fileId='a'.repeat(32);const fileBody='Workspace download fixture ✓\n';
const received=[]; let failConversation=false;
const server=http.createServer(async (req,res)=>{
 const url=new URL(req.url,'http://local'); const p=url.pathname; let data;
 if(p==='/__test/state') data={received,entries:entries.length,sessions};
 else if(p==='/__test/fail-conversation') {failConversation=url.searchParams.get('enabled')==='true';data={failConversation};}
 else if(p==='/api/config') data={authority:'',version:'1.0.0-rc.1-fixture'};
 else if(p==='/api/credentials/accounts') data=accounts;
 else if(/\/credential$/.test(p) && req.method==='PATCH') {let raw='';for await(const c of req) raw+=c;received.push({url:p,method:'PATCH',body:JSON.parse(raw)});data={};}
 else if(p.endsWith('/messages') && req.method==='POST') {let raw='';for await(const c of req) raw+=c;received.push({url:p,method:'POST',body:JSON.parse(raw)});data={deliveredVia:'injected'};}
 else if(p.endsWith('/messages')) {const now=new Date().toISOString();data=messages.map(m=>({...m,createdAt:now,deliveredAt:m.deliveredAt&&now}));}
 else if(p.endsWith('/convert')) {let raw='';for await(const c of req) raw+=c;const body=JSON.parse(raw);received.push({url:p,method:'POST',body});const s2=sessions.find(x=>p.includes(x.id));Object.assign(s2,{mode:'Interactive',phase:'Running',uiMode:body.uiMode,canConvertToInteractive:false,convertedFrom:'Autonomous'});data=s2;}
 else if(p.endsWith('/files')) data=[{id:fileId,name:'report-ü.txt',mimeType:'text/plain',size:Buffer.byteLength(fileBody),state:'Ready'}];
 else if(p.endsWith('/files/'+fileId+'/content')) {res.writeHead(200,{'Content-Type':'text/plain'});res.end(fileBody);return;}
 else if(p==='/api/sessions' && req.method==='POST') {
  let raw='';for await(const chunk of req) raw+=chunk;const body=JSON.parse(raw);
  data={...session,...body,id:'created-'+sessions.length};sessions.push(data);
 }
 else if(p==='/api/sessions') data=sessions;
 else if(p==='/api/sessions/shared') data=shared;
 else if(p==='/api/agents/allowed') data={agents:['Claude','Codex','Cursor','OpenClaw','OpenCode']};
 else if(p==='/api/credentials') data={};
 else if(p==='/api/mcp-servers') data=[];
 else if(p==='/api/projects') data=[];
 else if(p==='/api/admin/access') data={isAdmin:false};
 else if(p==='/api/shared/view-token/session') data={...session,title:'Shared read-only',accessRole:'Viewer',sharedBy:'owner@example.com'};
 else if(p.endsWith('/transcript')) {res.writeHead(200,{'Content-Type':'text/plain'});res.end('cleaned terminal text\n');return;}
 else if(p.includes('/empty-run/') && p.endsWith('/conversation')) data={source:'scrollback',entries:[],text:'',offset:0,nextOffset:0,length:0};
 else if(p.endsWith('/conversation')) {
  if(failConversation) {res.writeHead(503);res.end('Intentional browser-test failure');return;}
  const offset=Number(url.searchParams.get('offset')||0);const nextOffset=Math.min(entries.length,offset+8);
  data={source:'native',offset,nextOffset,length:entries.length,entries:entries.slice(offset,nextOffset)};
 }
 else if(p.endsWith('/permissions')) data=[];
 else if(p.endsWith('/models')) data={selected:'gpt-5.5-codex',switchMode:'live',canSwitch:!p.includes('/viewer-session/'),models:[{id:'gpt-5.5-codex',isDefault:true},{id:'gpt-5.5-codex-mini'}]};
 else if(p.endsWith('/provider-switch')) data={available:false,reason:'Provider replacement is available for idle sessions only (fixture).'};
 else if(p.endsWith('/files/capabilities')) data={canUpload:false,canRead:true,limits:{presentationPollMilliseconds:30000}};
 else if(p.endsWith('/files/presentation')) data=p.includes('/presented/')?{revision:1,fileId}:{revision:0,fileId:null};
 else {console.log('404',p);res.writeHead(404);res.end('Unknown fixture route: '+p);return;}
 res.writeHead(200,{'Content-Type':'application/json'});res.end(JSON.stringify(data));
});
const wss=new WebSocketServer({server});
wss.on('connection',(socket,req)=>{
 if(req.url.endsWith('/events')) return;
 if(req.url.endsWith('/browser')) return socket.close();
 const id=req.url.split('/')[3];const streaming=sessions.find(s=>s.id===id)?.uiMode==='chat' && req.url.endsWith('/terminal');
 const frame=value=>socket.send(JSON.stringify(value)+'\n');
 if(streaming) frame({type:'assistant',message:{id:'welcome',content:[{type:'text',text:'Streaming Workspace ready.'},{type:'tool_use',id:'tool-1',name:'Bash',input:{command:'npm test'}}]}});
 else socket.send('Browser test terminal ready\r\n');
 let prompt='';
 socket.on('message',raw=>{
  const message=JSON.parse(raw);
  if(message.type==='chat') {
   received.push({url:req.url,...message});
   frame({type:'agenthub',subtype:'chat_delivered',clientTurnId:message.clientTurnId});
   frame({type:'user',agenthub_echo:true,message:{content:[{type:'text',text:message.text}]}});
   frame({type:'assistant',message:{id:'reply-'+received.length,content:[{type:'text',text:'Streaming test reply: '+message.text}]}});
   frame({type:'result',subtype:'success',result:'done'});return;
  }
  if(message.type!=='input')return;
  received.push({url:req.url,...message});
  if(message.data.startsWith('\x1b[200~')) prompt=message.data.slice(6,-6);
  if(message.data==='\r') {entries.push({role:'user',text:prompt},{role:'assistant',text:'Test transport received the prompt.'});socket.send('\r\nFixture received: '+prompt+'\r\n');}
  if(message.data==='\x03') socket.send('^C\r\n');
 });
});
server.listen(8080,'127.0.0.1',()=>console.log('UI fixture on :8080 (explicit test data)'));
