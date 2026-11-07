import assert from 'node:assert/strict';
import crypto from 'node:crypto';
import { execFileSync, spawn } from 'node:child_process';
import { createRequire } from 'node:module';
const require = createRequire('/tmp/provider-switch-tools/package.json');
const WebSocket = require('ws'), { Terminal } = require('@xterm/headless');
const ns = 'agenthub-sessions', control = 'agenthub';
const kubectl = (...args) => execFileSync('kubectl', args, { encoding:'utf8',stdio:['pipe','pipe','pipe'],maxBuffer:8*1024*1024 });
const json = (...args) => JSON.parse(kubectl(...args,'-o','json'));
const key = Buffer.from(json('-n',control,'get','secret','agenthub-api-key').data['api-key'],'base64').toString();
const headers = {Authorization:'Bearer '+key,'Content-Type':'application/json'};
const sleep = ms => new Promise(resolve=>setTimeout(resolve,ms));
async function until(fn, description, attempts=600) {
 let error;
 for(let n=0;n<attempts;n++){try{const value=await fn();if(value)return value;}catch(caught){if(caught?.fatal)throw caught;error=caught;}await sleep(500);}
 throw new Error(description+': '+(error?.message??'timeout'));
}
async function api(route,method='GET',body,expected=200){
 const response=await fetch('http://127.0.0.1:18880/api/'+route,{method,headers,body:body===undefined?undefined:JSON.stringify(body),signal:AbortSignal.timeout(150000)});
 assert.equal(response.status,expected,method+' '+route+': '+await response.clone().text());
 const text=await response.text();return text?JSON.parse(text):null;
}
async function openTerminal(id,kind='terminal'){
 return until(async()=>{
  const term=new Terminal({cols:120,rows:36,allowProposedApi:true});
  const socket=new WebSocket('ws://127.0.0.1:18880/ws/sessions/'+id+'/'+kind,{headers:{Authorization:'Bearer '+key}});
  socket.on('message',data=>term.write(data.toString()));
  term.onData(data=>{if(socket.readyState===WebSocket.OPEN)socket.send(JSON.stringify({type:'input',data}));});
  try{await new Promise((resolve,reject)=>{
   const timer=setTimeout(()=>reject(new Error('no upstream terminal output')),10000);
   // Backend upgrade precedes its pod connection; wait for actual upstream bytes.
   socket.once('message',()=>{clearTimeout(timer);resolve();});
   socket.once('close',()=>{clearTimeout(timer);reject(new Error('upstream terminal closed'));});
   socket.once('error',error=>{clearTimeout(timer);reject(error);});
   socket.once('unexpected-response',(request,response)=>{clearTimeout(timer);response.resume();reject(new Error('terminal not ready'));});
  });}
  catch{socket.terminate();term.dispose();return null;}
  socket.send(JSON.stringify({type:'resize',cols:120,rows:36}));
  return {socket,close(){socket.close();term.dispose();}};
 },'connect actual terminal',240);
}
function apply(object){return execFileSync('kubectl',['apply','-f','-'],{input:JSON.stringify(object),encoding:'utf8',stdio:['pipe','pipe','pipe']});}
let id,terminal,watcher,shell;
const proofSecrets=[];
function seedSubscriptions(ownerKey){
 const future=Date.now()+86400000;
 const claims={email:'proof@example.com',exp:Math.floor(future/1000),'https://api.openai.com/auth':{chatgpt_account_id:'proof-account',chatgpt_user_id:'proof-user',user_id:'proof-user',chatgpt_plan_type:'plus'}};
 const idToken=Buffer.from('{"alg":"none"}').toString('base64url')+'.'+Buffer.from(JSON.stringify(claims)).toString('base64url')+'.proof';
 const native={
  claude:{claudeAiOauth:{accessToken:'synthetic-claude-subscription',refreshToken:'synthetic-refresh',expiresAt:future,scopes:['user:inference','user:profile','user:sessions:claude_code'],subscriptionType:'pro',rateLimitTier:'default_claude_pro'}},
  codex:{auth_mode:'chatgpt',tokens:{id_token:idToken,access_token:'synthetic-codex-subscription',refresh_token:'synthetic-refresh',account_id:'proof-account'},last_refresh:new Date().toISOString()}
 };
 for(const provider of ['claude','codex']){
  const name=provider+'-'+ownerKey;
  let existing=false;try{json('-n',ns,'get','secret',name);existing=true;}catch{}
  assert(!existing,'proof must not replace an existing provider credential store');
  apply({apiVersion:'v1',kind:'Secret',metadata:{name,namespace:ns,labels:{'agenthub.dev/owner':ownerKey}},type:'Opaque',
   data:{[provider==='claude'?'credentials.json':'auth.json']:Buffer.from(JSON.stringify(native[provider])).toString('base64')}});
  proofSecrets.push(name);
 }
}
try{
 await until(async()=>{await api('credentials');return true;},'backend ready');
 await api('credentials','PUT',{anthropicApiKey:'synthetic-claude-proof',openAiApiKey:'synthetic-codex-proof'},204);
 let ownerKey;
 for(const authMode of ['ApiKey','Subscription']){
  if(authMode==='Subscription')seedSubscriptions(ownerKey);
  const created=await api('sessions','POST',{title:'Native provider replacement '+authMode,agent:'Claude',authMode,mode:'Interactive',
   ...(authMode==='Subscription'?{credentialId:'default'}:{}),prompt:'SWITCH-PROOF-0',autoApprove:false,policy:{allowedTools:['Bash'],allowedCommands:['printf'],allowedMcpTools:[]},
   repos:['first','second'].map(name=>({url:process.env.PROOF_MODEL_URL+'/'+name+'.git',branch:'main'}))});
  id=created.id;const podName='session-'+id;
  const pod=()=>json('-n',ns,'get','pod',podName);
  const exec=script=>kubectl('-n',ns,'exec',podName,'-c','agent','--','/bin/sh','-ec',script);
  terminal=await openTerminal(id);
  await until(async()=>{const page=await api('sessions/'+id+'/conversation?offset=0&max=500');return page.entries?.some(entry=>entry.text.includes('Answer SWITCH-PROOF-0'));},'initial real CLI answer');
  const first=pod();ownerKey=first.metadata.labels['agenthub.dev/owner'];
  const claimName=first.spec.volumes.find(volume=>volume.name==='workspace').persistentVolumeClaim.claimName;
  const claimUid=json('-n',ns,'get','pvc',claimName).metadata.uid;
  const nativeIds=new Set([first.spec.containers.find(container=>container.name==='agent').env.find(item=>item.name==='AGENTHUB_CLAUDE_SESSION_ID').value]);
  exec("for name in first second; do printf 'dirty retained content\\n' >> /workspace/$name/tracked.txt; printf 'untracked retained content\\n' > /workspace/$name/untracked.txt; git -C /workspace/$name config proof.retained yes; done");
  const fingerprint=()=>exec('cd /workspace; find first second -type f -exec sha256sum {} \\; | sort');
  const baseline=fingerprint();
  await until(()=>pod().status.containerStatuses?.some(container=>container.name==='agent'&&container.ready),'source ready before cancellation proof');
  shell=await openTerminal(id,'shell');
  const cancelId=crypto.randomUUID();
  const cancelSelection={requestId:cancelId,target:{agent:'Codex',authMode,credentialId:authMode==='Subscription'?'default':null,modelEndpointUrl:null,modelName:null},
   handover:{target:'Codex',instructions:'Preserve policy.',task:'Cancellation must retain the source.'}};
  const cancelReview=await api('sessions/'+id+'/provider-switch/preview','POST',cancelSelection);
  await api('sessions/'+id+'/provider-switch','POST',{...cancelSelection,reviewHash:cancelReview.reviewHash});
  await api('sessions/'+id+'/messages','POST',{body:'CANCEL-QUEUED-'+authMode,priority:true});
  await until(async()=>{const value=await api('sessions/'+id+'/provider-switch');return value.operation?.progress.phase==='requested'&&value.operation.progress.error;},'known open shell refuses source preparation');
  await api('sessions/'+id+'/provider-switch/cancel','POST',{requestId:cancelId});
  await until(async()=>{const value=await api('sessions/'+id+'/provider-switch');return value.operation?.progress.phase==='cancelled';},'durable cancellation completes');
  assert.equal(pod().metadata.uid,first.metadata.uid);
  await until(()=>{const rows=JSON.parse(exec(`node -e 'fetch(${JSON.stringify(process.env.PROOF_MODEL_URL+'/proof/requests')}).then(response=>response.json()).then(value=>process.stdout.write(JSON.stringify(value)))'`));return rows.some(row=>row.agent==='claude'&&row.authMode===authMode&&row.containsCancelled);},'model receives queued source message after cancellation');
  shell.close();shell=null;
  console.log(authMode+': actual source shell refusal, cancellation and queued-message handback passed.');
  const watchEvents=[];let remainder='';
  watcher=spawn('kubectl',['-n',ns,'get','pods','-l','agenthub.dev/session='+id,'--watch','--output-watch-events','-o','jsonpath={.type}{"\\t"}{.object.metadata.uid}{"\\n"}'],{stdio:['ignore','pipe','ignore']});
  watcher.stdout.on('data',chunk=>{remainder+=chunk;const lines=remainder.split('\n');remainder=lines.pop();for(const line of lines){const [type,uid]=line.trim().split(/\s+/);watchEvents.push({type,uid});}});
  await until(()=>watchEvents.some(event=>event.type==='ADDED'&&event.uid===first.metadata.uid),'Kubernetes watch observes source before replacement');
  for(const [generation,target] of [[1,'Codex'],[2,'Claude']]){
   const source=await until(()=>{const value=pod();return value.status.phase==='Running'&&value.status.containerStatuses?.some(container=>container.name==='agent'&&container.ready)&&value;},'source kubelet readiness');
   const sourceToken=source.spec.containers.find(container=>container.name==='agent').env.find(item=>item.name==='AGENTHUB_CALLBACK_TOKEN').value;
   const sourceProvider=target==='Codex'?'claude':'codex';
   exec("printf 'private source home' > /home/agent/provider-proof-private; printf 'private native state' > /home/agent/."+sourceProvider+"/provider-proof-native");
   const selection={requestId:crypto.randomUUID(),target:{agent:target,authMode,credentialId:authMode==='Subscription'?'default':null,modelEndpointUrl:null,modelName:null},
    handover:{target,instructions:'Preserve the dirty repositories and approval policy. Earlier native context is omitted.',task:'Continue SWITCH-PROOF-'+generation,decisions:'Use the same visible conversation.',pendingWork:'Verify files.',references:['/workspace/first/tracked.txt','/workspace/second/untracked.txt']}};
   const review=await api('sessions/'+id+'/provider-switch/preview','POST',selection);
   assert.equal(review.capacity.source,'native-minimum');assert(review.text.includes('Omitted:'));assert(review.text.length<=16000);
   const started=await api('sessions/'+id+'/provider-switch','POST',{...selection,reviewHash:review.reviewHash});
   assert.equal(started.request.requestId,selection.requestId);
   const queued=await api('sessions/'+id+'/messages','POST',{body:'QUEUED-AFTER-HANDOVER-'+generation,priority:true});
   assert.equal(queued.deliveredVia,'inbox');
   terminal.close();terminal=null;
   let safePointRetries=0;
   await until(async()=>{
    const state=await api('sessions/'+id+'/provider-switch');
    if(state.operation?.progress.error){
     if(state.operation.progress.phase==='requested'&&state.operation.progress.error.startsWith('Finish the current turn')&&safePointRetries++<10){
      await sleep(1000);
      await api('sessions/'+id+'/provider-switch/retry','POST',{requestId:selection.requestId});
      return false;
     }
     throw Object.assign(new Error(state.operation.progress.error),{fatal:true});
    }
    try{const current=pod();return current.metadata.uid!==source.metadata.uid&&current.status.phase==='Running';}catch{return false;}
   },'actual replacement pod');
   terminal=await openTerminal(id);
   const finished=await until(async()=>{const state=await api('sessions/'+id+'/provider-switch');if(state.operation?.progress.error)throw Object.assign(new Error(state.operation.progress.error),{fatal:true});return state.operation?.progress.phase==='completed'&&state;},'complete durable replacement');
   const cutoff=finished.completedUsage?.find(item=>item.generation===generation-1);
   assert(cutoff?.inputTokens>0&&cutoff?.outputTokens>0,'graceful source quit must flush real native usage before the generation cutoff');
   const native=finished.operation.progress.targetNativeId;
   assert(native&&!nativeIds.has(native));nativeIds.add(native);
   const current=pod();
   assert.equal(current.spec.volumes.find(volume=>volume.name==='workspace').persistentVolumeClaim.claimName,claimName);
   assert.equal(json('-n',ns,'get','pvc',claimName).metadata.uid,claimUid);
   assert.equal(fingerprint(),baseline);
   const env=current.spec.containers.find(container=>container.name==='agent').env;
   assert.equal(env.find(item=>item.name==='AGENTHUB_STATE_GET_URL')?.value??'','');
   assert.equal(env.find(item=>item.name==='AGENTHUB_AUTO_APPROVE')?.value,'0');
   assert.deepEqual(JSON.parse(env.find(item=>item.name==='AGENTHUB_ALLOWED_TOOLS').value),['Bash']);
   assert(!env.some(item=>item.name===(target==='Codex'?'ANTHROPIC_API_KEY':'CODEX_API_KEY')));
   if(authMode==='Subscription')assert(!env.some(item=>['ANTHROPIC_API_KEY','CODEX_API_KEY'].includes(item.name)));
   const check=JSON.parse(exec(`node -e 'const fs=require("node:fs");const env=process.env;const own=${JSON.stringify(target.toLowerCase())};const other=own==="codex"?"claude":"codex";const file=env.HOME+(own==="codex"?"/.codex/auth.json":"/.claude/.credentials.json");const auth=fs.existsSync(file)?fs.readFileSync(file,"utf8"):"";const expected="synthetic-"+own+(${JSON.stringify(authMode)}==="Subscription"?"-subscription":"-proof");process.stdout.write(JSON.stringify({privateHome:fs.existsSync(env.HOME+"/provider-proof-private"),otherAuth:fs.existsSync(env.HOME+(other==="claude"?"/.claude/.credentials.json":"/.codex/auth.json")),otherHistory:fs.existsSync(env.HOME+(other==="claude"?"/.claude/projects":"/.codex/sessions")),nativeSentinel:[own,other].some(provider=>fs.existsSync(env.HOME+"/."+provider+"/provider-proof-native")),oppositeMount:fs.existsSync("/secrets/"+other),oppositeKeyFile:fs.existsSync("/secrets/creds/"+(own==="codex"?"anthropic_api_key":"openai_api_key")),oppositeKey:Boolean(env[own==="codex"?"ANTHROPIC_API_KEY":"CODEX_API_KEY"]),ownAuth:auth.includes(expected)||Object.values(env).includes(expected)}));'`));
   assert.deepEqual(check,{privateHome:false,otherAuth:false,otherHistory:false,nativeSentinel:false,oppositeMount:false,oppositeKeyFile:false,oppositeKey:false,ownAuth:true});
   await until(()=>{
    const nativeRequests=JSON.parse(exec(`node -e 'fetch(${JSON.stringify(process.env.PROOF_MODEL_URL+'/proof/requests')}).then(response=>response.json()).then(value=>process.stdout.write(JSON.stringify(value)))'`));
    return nativeRequests.some(row=>row.marker==='SWITCH-PROOF-'+generation&&row.agent===target.toLowerCase()&&row.authMode===authMode);
   },'real native inference with selected target credential mode');
   const late=await fetch('http://127.0.0.1:18880/internal/sessions/'+id+'/status',{method:'POST',headers:{'Content-Type':'application/json','X-Agent-Token':sourceToken},body:JSON.stringify({status:'Failed'})});assert.equal(late.status,401);
   const page=await until(async()=>{const approvals=await api('sessions/'+id+'/permissions');for(const approval of approvals)await api('sessions/'+id+'/permissions/'+approval.id,'POST',{decision:'deny'});const rows=JSON.parse(exec(`node -e 'fetch(${JSON.stringify(process.env.PROOF_MODEL_URL+'/proof/requests')}).then(response=>response.json()).then(value=>process.stdout.write(JSON.stringify(value)))'`));return rows.some(row=>row.marker==='SWITCH-PROOF-'+generation&&row.agent===target.toLowerCase()&&row.authMode===authMode&&row.containsQueued)&&await api('sessions/'+id+'/conversation?offset=0&max=500');},'model receives queued message after native handover');
   assert.equal(exec('test ! -e /workspace/provider-proof-not-authorized && printf absent'),'absent');
   if(generation===2)await until(()=>{
    const records=JSON.parse(exec(`node -e 'fetch(${JSON.stringify(process.env.PROOF_MODEL_URL+'/proof/requests')}).then(response=>response.json()).then(value=>process.stdout.write(JSON.stringify(value)))'`));
    return records.some(record=>record.marker==='SWITCH-PROOF-2'&&record.deniedTool);
   },'real native tool denial receipt');
   const users=page.entries.filter(entry=>entry.generation===generation&&entry.role==='user');
   assert.equal(users[0].text,review.text);
   assert(page.entries.some(entry=>entry.text.includes('SWITCH-PROOF-0')));
   const archive=await fetch('http://127.0.0.1:18880/api/sessions/'+id+'/state',{headers:{Authorization:'Bearer '+key}});assert.equal(archive.status,200);
   const listing=execFileSync('tar',['tzf','-'],{input:Buffer.from(await archive.arrayBuffer()),encoding:'utf8',maxBuffer:8*1024*1024});
   assert(listing.includes(native));assert(!/(^|\/)\.credentials\.json$|(^|\/)auth\.json$/m.test(listing));
   await sleep(500);
   const gone=watchEvents.findIndex(event=>event.type==='DELETED'&&event.uid===source.metadata.uid);
   const added=watchEvents.findIndex(event=>event.type==='ADDED'&&event.uid===current.metadata.uid);
   assert(gone>=0&&added>gone,'actual kubelet/API watch must observe source deletion before target creation');
   assert.equal((await api('sessions/'+id)).agent,target);
   console.log(authMode+' generation '+generation+': actual '+target+' pod, same dirty repos/PVC, fresh native/home/target-only auth, handover first, queued input afterwards, retired callback401 and native archive passed.');
  }
  watcher.kill('SIGTERM');watcher=null;terminal.close();terminal=null;
  await api('sessions/'+id,'DELETE',undefined,204);id=null;
  await until(()=>{try{json('-n',ns,'get','pvc',claimName);return false;}catch{return true;}},'workspace cleanup');
 }
 console.log('PASS: real backend + Postgres + object storage + kubelet + pinned CLIs, API-key and synthetic subscription Claude→Codex→Claude, both native model lists without supplied context-window metadata.');
}catch(error){
 if(id){try{console.error('Pod status:',JSON.stringify(json('-n',ns,'get','pod','session-'+id).status));}catch{}try{console.error(kubectl('-n',ns,'logs','session-'+id,'-c','agent','--tail=100'));}catch{}try{console.error('Operation:',JSON.stringify(await api('sessions/'+id+'/provider-switch')));}catch{}try{console.error('Model receipts:',kubectl('-n',ns,'exec','session-'+id,'-c','agent','--','node','-e',`fetch(${JSON.stringify(process.env.PROOF_MODEL_URL+'/proof/requests')}).then(response=>response.json()).then(value=>process.stdout.write(JSON.stringify(value)))`));}catch{}}
 throw error;
}finally{
 shell?.close();terminal?.close();watcher?.kill('SIGTERM');
 if(id){try{await api('sessions/'+id,'DELETE',undefined,204);}catch{console.error('Session cleanup held; inspect retained claim in the disposable cluster.');}}
 for(const name of proofSecrets){try{kubectl('-n',ns,'delete','secret',name);}catch{}}
}
