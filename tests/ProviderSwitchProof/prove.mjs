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
const backendUrl=process.env.PROOF_BACKEND_URL||'http://127.0.0.1:18880';
const repositoryMode = process.env.PROOF_REPOSITORY === '1';
let recovery=null;
async function until(fn, description, attempts=process.env.PROOF_RECOVERY==='1'?2400:600) {
 let error;
 for(let n=0;n<attempts;n++){try{recovery?.check();const value=await fn();if(value)return value;}catch(caught){if(caught?.fatal)throw caught;error=caught;}await sleep(500);}
 throw new Error(description+': '+(error?.message??'timeout'));
}
async function api(route,method='GET',body,expected=200){
 const idempotent=method==='GET'||(route.includes('/provider-switch')&&body?.requestId);
 const attempts=recovery&&idempotent?120:1;
 let last;
 for(let attempt=0;attempt<attempts;attempt++){
  recovery?.check();
  try{
   const response=await fetch(backendUrl+'/api/'+route,{method,headers,body:body===undefined?undefined:JSON.stringify(body),signal:AbortSignal.timeout(150000)});
   const text=await response.text();
   if(recovery&&idempotent&&response.status===409&&text.includes('Session maintenance is already in progress.')){await sleep(500);continue;}
   assert.equal(response.status,expected,method+' '+route+': '+text);
   return text?JSON.parse(text):null;
  }catch(error){
   if(!recovery||!idempotent||!['TypeError','TimeoutError','AbortError'].includes(error.name))throw error;
   last=error;await sleep(500);
  }
 }
 throw last||new Error('Idempotent owner request remained busy: '+route);
}
async function openTerminal(id,kind='terminal'){
 return until(async()=>{
  const term=new Terminal({cols:120,rows:36,allowProposedApi:true});
  const socket=new WebSocket(backendUrl.replace(/^http/,'ws')+'/ws/sessions/'+id+'/'+kind,{headers:{Authorization:'Bearer '+key}});
  socket.on('message',data=>term.write(data.toString()));
  term.onData(data=>{if(socket.readyState===WebSocket.OPEN)socket.send(JSON.stringify({type:'input',data}));});
  try{await new Promise((resolve,reject)=>{
   const timer=setTimeout(()=>reject(new Error('no upstream terminal output')),10000);
   // Backend upgrade precedes its pod connection; wait for actual upstream bytes.
   socket.once('message',()=>{clearTimeout(timer);resolve();});
   socket.once('close',(code,reason)=>{clearTimeout(timer);reject(new Error('upstream terminal closed '+code+': '+reason));});
   socket.once('error',error=>{clearTimeout(timer);reject(error);});
   socket.once('unexpected-response',(request,response)=>{clearTimeout(timer);response.resume();reject(new Error('terminal HTTP '+response.statusCode));});
  });}
  catch(error){socket.terminate();term.dispose();throw error;}
  socket.send(JSON.stringify({type:'resize',cols:120,rows:36}));
  return {socket,screen(){return Array.from({length:term.rows},(_,index)=>term.buffer.active.getLine(term.buffer.active.viewportY+index)?.translateToString(true)??'');},close(){socket.close();term.dispose();}};
 },'connect actual terminal',240);
}
async function openSourceShell(id){
 const program=`const WebSocket=require('/opt/session-agent/node_modules/ws');const socket=new WebSocket('ws://127.0.0.1:7681/shell');socket.once('message',()=>process.stdout.write('shell-ready\\n'));socket.on('error',()=>process.exit(1));process.stdin.resume();process.stdin.on('end',()=>socket.close());socket.on('close',()=>process.exit(0));`;
 const child=spawn('kubectl',['-n',ns,'exec','-i','session-'+id,'-c','agent','--','node','-e',program],{stdio:['pipe','pipe','pipe']});
 let output='',error='';child.stdout.on('data',bytes=>output+=bytes);child.stderr.on('data',bytes=>error+=bytes);
 try{await until(()=>{if(child.exitCode!==null)throw Object.assign(new Error('source loopback shell exited: '+error),{fatal:true});return output.includes('shell-ready');},'actual source loopback shell',120);}
 catch(error){child.stdin.end();child.kill('SIGTERM');throw error;}
 return {async close(){child.stdin.end();await until(()=>child.exitCode!==null,'source shell cleanup',40);}};
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
if(process.env.PROOF_RECOVERY==='1'&&process.env.PROOF_RECOVERY_SCOPE==='journal'){
 const {createRecoveryFaults}=await import('../ProviderRecoveryProof/faults.mjs');
 recovery=createRecoveryFaults({api,json});
}
try{
 await until(async()=>{await api('credentials');return true;},'backend ready');
 await api('credentials','PUT',{anthropicApiKey:'synthetic-claude-proof',openAiApiKey:'synthetic-codex-proof'},204);
 let ownerKey;
 if(process.env.PROOF_RECOVERY_SCOPE==='held'){const {proveHeldRecovery}=await import('../ProviderRecoveryProof/held.mjs');await proveHeldRecovery({api,json,kubectl,openTerminal,openSourceShell,until,seedSubscriptions,headers,backendUrl,ns,control});}
 else for(const authMode of ['ApiKey','Subscription']){
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
  const volume=first.spec.volumes.find(volume=>volume.name==='workspace');
  const claimName=volume.persistentVolumeClaim?.claimName;
  const claimUid=claimName?json('-n',ns,'get','pvc',claimName).metadata.uid:null;
  if(repositoryMode){assert(volume.emptyDir);assert(!claimName);assert(!json('-n',ns,'get','pvc').items.some(item=>item.metadata.labels?.['agenthub.dev/session']===id));}

  const nativeIds=new Set([first.spec.containers.find(container=>container.name==='agent').env.find(item=>item.name==='AGENTHUB_CLAUDE_SESSION_ID').value]);
  if(repositoryMode)exec('for name in first second; do git -C /workspace/$name checkout -b work/proof --track origin/main; done');
  else exec("for name in first second; do printf 'dirty retained content\\n' >> /workspace/$name/tracked.txt; printf 'untracked retained content\\n' > /workspace/$name/untracked.txt; git -C /workspace/$name config proof.retained yes; done");
  const fingerprint=()=>repositoryMode?exec('for name in first second; do git -C /workspace/$name rev-parse HEAD; git -C /workspace/$name symbolic-ref --short HEAD; sha256sum /workspace/$name/tracked.txt; done'):exec('cd /workspace; find first second -type f -exec sha256sum {} \\; | sort');
  const baseline=fingerprint();
  await until(()=>pod().status.containerStatuses?.some(container=>container.name==='agent'&&container.ready),'source ready before cancellation proof');
  shell=await openSourceShell(id);
  const cancelId=crypto.randomUUID();
  const cancelSelection={requestId:cancelId,target:{agent:'Codex',authMode,credentialId:authMode==='Subscription'?'default':null,modelEndpointUrl:null,modelName:null},
   handover:{target:'Codex',instructions:'Preserve policy.',task:'Cancellation must retain the source.'}};
  if(repositoryMode){const saved=await api('sessions/'+id+'/provider-switch/repositories','POST');assert(saved.repositories.every(item=>!item.error));cancelSelection.repositoryHash=saved.reviewHash;cancelSelection.acknowledgeOmissions=true;}
  const cancelReview=await api('sessions/'+id+'/provider-switch/preview','POST',cancelSelection);
  await api('sessions/'+id+'/provider-switch','POST',{...cancelSelection,reviewHash:cancelReview.reviewHash});
  await api('sessions/'+id+'/messages','POST',{body:'CANCEL-QUEUED-'+authMode,priority:true});
  await until(async()=>{const value=await api('sessions/'+id+'/provider-switch');return value.operation?.progress.phase==='requested'&&value.operation.progress.error;},'known open shell refuses source preparation');
  await recovery?.arm(id,cancelId,true);
  await api('sessions/'+id+'/provider-switch/cancel','POST',{requestId:cancelId});
  await until(async()=>{const value=await api('sessions/'+id+'/provider-switch');return value.operation?.progress.phase==='cancelled';},'durable cancellation completes');
  await recovery?.finish(cancelId);
  assert.equal(pod().metadata.uid,first.metadata.uid);
  await until(()=>{const rows=JSON.parse(exec(`node -e 'fetch(${JSON.stringify(process.env.PROOF_MODEL_URL+'/proof/requests')}).then(response=>response.json()).then(value=>process.stdout.write(JSON.stringify(value)))'`));return rows.some(row=>row.agent==='claude'&&row.authMode===authMode&&row.containsCancelled);},'model receives queued source message after cancellation');
  await shell.close();shell=null;
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
   if(repositoryMode){
    exec("printf dirty >> /workspace/first/tracked.txt; printf unsaved > /workspace/second/untracked.txt");
    const dirty=await api('sessions/'+id+'/provider-switch/repositories','POST');
    assert.equal(dirty.repositories[0].trackedChanges,1);assert.equal(dirty.repositories[1].untracked,1);
    assert(dirty.repositories.every(item=>item.error));
    exec("git -C /workspace/first restore tracked.txt; rm /workspace/second/untracked.txt; printf ignored-source-only > /workspace/first/.env; printf outside-source-only > /workspace/local-only.txt");
    const saved=await api('sessions/'+id+'/provider-switch/repositories','POST');
    assert(saved.repositories.every(item=>!item.error));assert.equal(saved.repositories[0].ignored.count,1);
    assert(saved.outside.names.includes('local-only.txt'));
    selection.repositoryHash=saved.reviewHash;selection.acknowledgeOmissions=true;
   }
   const review=await api('sessions/'+id+'/provider-switch/preview','POST',selection);
   assert.equal(review.capacity.source,'native-minimum');assert(review.text.includes('Omitted:'));assert(review.text.length<=16000);
   await recovery?.arm(id,selection.requestId);
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
   await recovery?.finish(selection.requestId);
   const cutoff=finished.completedUsage?.find(item=>item.generation===generation-1);
   assert(cutoff?.inputTokens>0&&cutoff?.outputTokens>0,'graceful source quit must flush real native usage before the generation cutoff');
   const native=finished.operation.progress.targetNativeId;
   assert(native&&!nativeIds.has(native));nativeIds.add(native);
   const current=pod();
   if(repositoryMode){
    assert(current.spec.volumes.find(volume=>volume.name==='workspace').emptyDir);
    assert(!json('-n',ns,'get','pvc').items.some(item=>item.metadata.labels?.['agenthub.dev/session']===id));
    assert.equal(exec('test ! -e /workspace/first/.env && test ! -e /workspace/local-only.txt && printf omitted'),'omitted');
   }else{
    assert.equal(current.spec.volumes.find(volume=>volume.name==='workspace').persistentVolumeClaim.claimName,claimName);
    assert.equal(json('-n',ns,'get','pvc',claimName).metadata.uid,claimUid);
   }
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
   const late=await fetch(backendUrl+'/internal/sessions/'+id+'/status',{method:'POST',headers:{'Content-Type':'application/json','X-Agent-Token':sourceToken},body:JSON.stringify({status:'Failed'})});assert.equal(late.status,401);
   const page=await until(async()=>{const approvals=await api('sessions/'+id+'/permissions');for(const approval of approvals)await api('sessions/'+id+'/permissions/'+approval.id,'POST',{decision:'deny'});const rows=JSON.parse(exec(`node -e 'fetch(${JSON.stringify(process.env.PROOF_MODEL_URL+'/proof/requests')}).then(response=>response.json()).then(value=>process.stdout.write(JSON.stringify(value)))'`));return rows.some(row=>row.marker==='SWITCH-PROOF-'+generation&&row.agent===target.toLowerCase()&&row.authMode===authMode&&row.containsQueued)&&await api('sessions/'+id+'/conversation?offset=0&max=500');},'model receives queued message after native handover');
   assert.equal(exec('test ! -e /workspace/provider-proof-not-authorized && printf absent'),'absent');
   if(generation===2)await until(()=>{
    const records=JSON.parse(exec(`node -e 'fetch(${JSON.stringify(process.env.PROOF_MODEL_URL+'/proof/requests')}).then(response=>response.json()).then(value=>process.stdout.write(JSON.stringify(value)))'`));
    return records.some(record=>record.marker==='SWITCH-PROOF-2'&&record.deniedTool);
   },'real native tool denial receipt');
   const users=page.entries.filter(entry=>entry.generation===generation&&entry.role==='user');
   assert.equal(users[0].text,review.text);
   assert(page.entries.some(entry=>entry.text.includes('SWITCH-PROOF-0')));
   const archive=await fetch(backendUrl+'/api/sessions/'+id+'/state',{headers:{Authorization:'Bearer '+key}});assert.equal(archive.status,200);
   const archiveBytes=Buffer.from(await archive.arrayBuffer());
   const listing=execFileSync('tar',['tzf','-'],{input:archiveBytes,encoding:'utf8',maxBuffer:8*1024*1024});
   assert(listing.includes(native));if(repositoryMode){
    assert(!listing.includes('local-only')&&!listing.includes('.env'));
    const contents=execFileSync('tar',['xOzf','-'],{input:archiveBytes,maxBuffer:64*1024*1024});
    assert(!contents.includes(Buffer.from('ignored-source-only'))&&!contents.includes(Buffer.from('outside-source-only')));
   }assert(!/(^|\/)\.credentials\.json$|(^|\/)auth\.json$/m.test(listing));
   await sleep(500);
   const gone=watchEvents.findIndex(event=>event.type==='DELETED'&&event.uid===source.metadata.uid);
   const added=watchEvents.findIndex(event=>event.type==='ADDED'&&event.uid===current.metadata.uid);
   assert(gone>=0&&added>gone,'actual kubelet/API watch must observe source deletion before target creation');
   assert.equal((await api('sessions/'+id)).agent,target);
   console.log(authMode+' generation '+generation+': actual '+target+' pod, '+(repositoryMode?'exact clean commits/branches without PVC':'same dirty repos/PVC')+', fresh native/home/target-only auth, handover first, queued input afterwards, retired callback401 and native archive passed.');
   if(process.env.PROOF_RECOVERY==='1'){
    terminal.close();terminal=null;
    await api('sessions/'+id+'/pause','POST');
    await until(()=>{try{pod();return false;}catch{return true;}},'completed native pod pause');
    await api('sessions/'+id+'/resume','POST');
    const resumed=await until(()=>{const value=pod();return value.status.phase==='Running'&&value.status.containerStatuses?.some(item=>item.name==='agent'&&item.ready)&&value;},'completed native pod resume');
    assert.notEqual(resumed.metadata.uid,current.metadata.uid);
    if(claimName){assert.equal(json('-n',ns,'get','pvc',claimName).metadata.uid,claimUid);}
    else{assert(pod().spec.volumes.find(volume=>volume.name==='workspace').emptyDir);}
    assert.equal(fingerprint(),baseline);
    const restoredEnv=resumed.spec.containers.find(item=>item.name==='agent').env;
    assert.equal(restoredEnv.find(item=>item.name==='AGENTHUB_REQUIRE_NATIVE_RESUME').value,'1');
    assert(!restoredEnv.find(item=>item.name==='AGENTHUB_PROMPT').valueFrom);
    assert.equal(restoredEnv.find(item=>item.name==='AGENTHUB_PROMPT').value??'','');
    assert.equal(exec(`node -e 'process.stdout.write(String(!process.env.AGENTHUB_PROMPT))'`),'true');
    terminal=await openTerminal(id);
    await until(()=>terminal.screen().some(line=>/^\s*[›❯]/.test(line)&&!line.includes('SWITCH-PROOF-')),'resumed native composer');
    const nativeState=JSON.parse(exec(`node -e 'fetch("http://127.0.0.1:7681/agenthub/provider-switch",{headers:{"X-Agent-Token":process.env.AGENTHUB_CALLBACK_TOKEN}}).then(response=>response.json()).then(value=>process.stdout.write(JSON.stringify(value)))'`));
    assert.equal(nativeState.nativeId,native);
    const followup='OWNER-AFTER-RESUME-'+authMode+'-'+generation;
    terminal.socket.send(JSON.stringify({type:'input',data:followup}));await sleep(200);
    terminal.socket.send(JSON.stringify({type:'input',data:'\r'}));
    const continued=await until(async()=>{const value=await api('sessions/'+id+'/conversation?offset=0&max=500');return value.entries.some(entry=>entry.generation===generation&&entry.role==='user'&&entry.text.includes(followup))&&value;},'continued owner turn persists in same native generation');
    assert.equal(continued.entries.filter(entry=>entry.generation===generation&&entry.role==='user'&&entry.text===review.text).length,1);
    assert.equal((await api('sessions/'+id)).executionGeneration,generation);
    console.log(authMode+' generation '+generation+': real native archive resume, same native identity/code/history, one handover and new owner turn passed.');
    if(process.env.PROOF_RECOVERY_SCOPE==='runtime'){
     await until(async()=>{const value=await api('sessions/'+id+'/conversation?offset=0&max=500');const at=value.entries.findIndex(entry=>entry.generation===generation&&entry.role==='user'&&entry.text.includes(followup));return at>=0&&value.entries.slice(at+1).some(entry=>entry.generation===generation&&entry.role==='assistant');},'owner turn finishes before native death');
     assert.equal(resumed.spec.restartPolicy,'Never');terminal.close();terminal=null;
     const checkpointId=crypto.randomUUID();
     const checkpointProgram=`const requestId=${JSON.stringify(checkpointId)};fetch("http://127.0.0.1:7681/agenthub/provider-switch",{method:"POST",headers:{"X-Agent-Token":process.env.AGENTHUB_CALLBACK_TOKEN,"Content-Type":"application/json"},body:JSON.stringify({requestId,action:"prepare"})}).then(async response=>process.stdout.write(JSON.stringify({status:response.status,body:await response.json()})))`;
     await until(()=>{const value=JSON.parse(kubectl('-n',ns,'exec',podName,'-c','agent','--','node','-e',checkpointProgram));return value.status===200&&value.body.checkpointed;},'acknowledged native checkpoint before abrupt container loss');
     const cancelled=JSON.parse(kubectl('-n',ns,'exec',podName,'-c','agent','--','node','-e',`fetch("http://127.0.0.1:7681/agenthub/provider-switch",{method:"DELETE",headers:{"X-Agent-Token":process.env.AGENTHUB_CALLBACK_TOKEN,"Content-Type":"application/json"},body:JSON.stringify({requestId:${JSON.stringify(checkpointId)}})}).then(async response=>process.stdout.write(JSON.stringify({status:response.status,body:await response.json()})))`));
     assert.equal(cancelled.status,200);
     const containerId=pod().status.containerStatuses.find(item=>item.name==='agent').containerID.replace(/^containerd:\/\//,'');
     assert(/^[0-9a-f]{64}$/.test(containerId),'fault only the recorded disposable session container');
     execFileSync('sudo',['k3s','ctr','-n','k8s.io','tasks','kill','--signal','SIGKILL',containerId],{stdio:'pipe'});
     const failed=await until(async()=>{const value=pod(),agent=value.status.containerStatuses?.find(item=>item.name==='agent');return agent?.state?.terminated&&(await api('sessions/'+id)).phase==='Failed'&&value;},'real kubelet observes failed native container');
     assert.equal(failed.metadata.uid,resumed.metadata.uid);assert.equal(failed.status.containerStatuses.find(item=>item.name==='agent').restartCount,0);
     await sleep(5000);
     const held=pod();assert.equal(held.metadata.uid,resumed.metadata.uid);assert.equal(held.status.containerStatuses.find(item=>item.name==='agent').restartCount,0);
     assert(held.status.containerStatuses.find(item=>item.name==='agent').state.terminated,'Never policy prevents automatic same-pod replay');
     await api('sessions/'+id+'/resume','POST');
     const recovered=await until(()=>{const value=pod();return value.metadata.uid!==held.metadata.uid&&value.status.containerStatuses?.some(item=>item.name==='agent'&&item.ready)&&value;},'owner explicitly resumes failed native generation');
     if(claimName){assert.equal(json('-n',ns,'get','pvc',claimName).metadata.uid,claimUid);}
     else{assert(pod().spec.volumes.find(volume=>volume.name==='workspace').emptyDir);}
     assert.equal(fingerprint(),baseline);
     const recoveredEnv=recovered.spec.containers.find(item=>item.name==='agent').env;
     assert.equal(recoveredEnv.find(item=>item.name==='AGENTHUB_REQUIRE_NATIVE_RESUME').value,'1');
     assert(!recoveredEnv.find(item=>item.name==='AGENTHUB_PROMPT').valueFrom);
     assert.equal(recoveredEnv.find(item=>item.name==='AGENTHUB_PROMPT').value??'','');
     assert.equal(exec(`node -e 'process.stdout.write(String(!process.env.AGENTHUB_PROMPT))'`),'true');
     terminal=await openTerminal(id);
     await until(()=>terminal.screen().some(line=>/^\s*[›❯]/.test(line)&&!line.includes('SWITCH-PROOF-')),'recovered native composer');
     const recoveredState=JSON.parse(exec(`node -e 'fetch("http://127.0.0.1:7681/agenthub/provider-switch",{headers:{"X-Agent-Token":process.env.AGENTHUB_CALLBACK_TOKEN}}).then(response=>response.json()).then(value=>process.stdout.write(JSON.stringify(value)))'`));
     assert.equal(recoveredState.nativeId,native);
     const afterCrash='OWNER-AFTER-CRASH-'+authMode+'-'+generation;
     terminal.socket.send(JSON.stringify({type:'input',data:afterCrash}));await sleep(200);terminal.socket.send(JSON.stringify({type:'input',data:'\r'}));
     const history=await until(async()=>{const value=await api('sessions/'+id+'/conversation?offset=0&max=500');return value.entries.some(entry=>entry.generation===generation&&entry.role==='user'&&entry.text.includes(afterCrash))&&value;},'new owner turn after explicit crash recovery');
     assert.equal(history.entries.filter(entry=>entry.generation===generation&&entry.role==='user'&&entry.text===review.text).length,1);
     assert(history.entries.some(entry=>entry.generation===generation&&entry.role==='user'&&entry.text.includes(followup)));
     await until(()=>{const receipts=JSON.parse(exec(`node -e 'fetch(${JSON.stringify(process.env.PROOF_MODEL_URL+'/proof/requests')}).then(response=>response.json()).then(value=>process.stdout.write(JSON.stringify(value)))'`));return receipts.some(item=>item.agent===target.toLowerCase()&&item.authMode===authMode&&item.marker==='SWITCH-PROOF-'+generation&&item.containsAfterCrash&&item.containsResumed);},'post-crash model receives checkpointed owner context');
     assert.equal((await api('sessions/'+id)).executionGeneration,generation);
     console.log(authMode+' generation '+generation+': actual container/pod crash via containerd task SIGKILL, Never policy/no same-pod restart, owner resume with same native identity/code/history and one handover passed.');
    }

   }

  }
  watcher.kill('SIGTERM');watcher=null;terminal.close();terminal=null;
  await api('sessions/'+id,'DELETE',undefined,204);id=null;
  if(claimName)await until(()=>{try{json('-n',ns,'get','pvc',claimName);return false;}catch{return true;}},'workspace cleanup');
 }
 console.log(process.env.PROOF_RECOVERY_SCOPE==='held'?'PASS: held credential/claim/checkpoint/target-loss cases against real native runtimes and storage/network policy.':'PASS: real backend + Postgres + object storage + kubelet + pinned CLIs, API-key and synthetic subscription Claude→Codex→Claude, both native model lists without supplied context-window metadata.');
}catch(error){
 console.error('Native provider proof failed:',error.message);
 recovery?.diagnostics();
 if(id){try{console.error('Pod status:',JSON.stringify(json('-n',ns,'get','pod','session-'+id).status));}catch{}try{console.error(kubectl('-n',ns,'logs','session-'+id,'-c','agent','--tail=100'));}catch{}try{console.error('Operation:',JSON.stringify(await api('sessions/'+id+'/provider-switch')));}catch{}try{console.error('Model receipts:',kubectl('-n',ns,'exec','session-'+id,'-c','agent','--','node','-e',`fetch(${JSON.stringify(process.env.PROOF_MODEL_URL+'/proof/requests')}).then(response=>response.json()).then(value=>process.stdout.write(JSON.stringify(value)))`));}catch{}}
 throw error;
}finally{
 await recovery?.close();
 await shell?.close();terminal?.close();watcher?.kill('SIGTERM');
 if(id){try{await api('sessions/'+id,'DELETE',undefined,204);}catch{console.error('Session cleanup held; inspect retained claim in the disposable cluster.');}}
 for(const name of proofSecrets){try{kubectl('-n',ns,'delete','secret',name);}catch{}}
}
