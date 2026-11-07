// Exercise real owner refusals and checkpoint/target-loss holds without changing application code.
import assert from 'node:assert/strict';
import crypto from 'node:crypto';
import {execFileSync} from 'node:child_process';
export const fingerprintScript='cd "$1"; find first second -type f -exec sha256sum {} \\; | sort';
export async function proveHeldRecovery({api,json,kubectl,openTerminal,openSourceShell,until,seedSubscriptions,headers,backendUrl,ns,control}){
 const replace=object=>{delete object.metadata.managedFields;return execFileSync('kubectl',['replace','-f','-'],{input:JSON.stringify(object),encoding:'utf8',stdio:['pipe','pipe','pipe']});};
 const create=object=>{delete object.metadata.resourceVersion;delete object.metadata.uid;delete object.metadata.managedFields;delete object.metadata.creationTimestamp;return execFileSync('kubectl',['create','-f','-'],{input:JSON.stringify(object),encoding:'utf8',stdio:['pipe','pipe','pipe']});};
 const policyName='allow-agent-to-object-storage';
 const originalSelector=json('-n',ns,'get','networkpolicy',policyName).spec.podSelector;
 const selector=value=>{const policy=json('-n',ns,'get','networkpolicy',policyName);policy.spec.podSelector=value;replace(policy);};
 const sql=query=>kubectl('-n',control,'exec','postgres-0','-c','postgres','--','psql','-q','-X','-A','-t','-v','ON_ERROR_STOP=1','-U','agenthub','-d','agenthub','-c',query).trim();
 async function denied(id,route,body,reason){
  const response=await fetch(backendUrl+'/api/sessions/'+id+'/provider-switch'+route,{method:'POST',headers,body:JSON.stringify(body)});
  const text=await response.text();assert.equal(response.status,409,text);assert.match(text,reason);
 }
 async function inspectWorkspace(id,claim,baseline){
  const inspector='provider-proof-inspect-'+id;
    create({apiVersion:'v1',kind:'Pod',metadata:{name:inspector,namespace:ns,labels:{'agenthub.dev/session':id}},spec:{restartPolicy:'Never',automountServiceAccountToken:false,
     securityContext:{runAsNonRoot:true,runAsUser:1000,runAsGroup:1000,fsGroup:1000,seccompProfile:{type:'RuntimeDefault'}},
     containers:[{name:'inspect',image:'example.com/provider-switch-codex:local',imagePullPolicy:'IfNotPresent',command:['/bin/sh','-ec',fingerprintScript,'proof','/workspace'],securityContext:{readOnlyRootFilesystem:true,allowPrivilegeEscalation:false,capabilities:{drop:['ALL']}},volumeMounts:[{name:'workspace',mountPath:'/workspace',readOnly:true}]}],volumes:[{name:'workspace',persistentVolumeClaim:{claimName:claim,readOnly:true}}]}});
    try{await until(()=>{const value=json('-n',ns,'get','pod',inspector);const state=value.status.containerStatuses?.find(item=>item.name==='inspect')?.state?.terminated;if(state&&state.exitCode!==0)throw Object.assign(new Error('read-only workspace inspector failed'),{fatal:true});return state;},'read-only retained workspace verification');assert.equal(kubectl('-n',ns,'logs',inspector,'-c','inspect'),baseline);}
    finally{kubectl('-n',ns,'delete','pod',inspector,'--wait=true');}
 }
 let id,terminal,subscriptions=false,cordonedNode;
 try{
  for(const sourceAgent of ['Claude','Codex'])for(const loss of [false,true,'source','schedule']){
   selector(originalSelector);
   const target=sourceAgent==='Claude'?'Codex':'Claude';
   const created=await api('sessions','POST',{title:'Held recovery '+sourceAgent+' '+loss,agent:sourceAgent,authMode:'ApiKey',mode:'Interactive',prompt:'SWITCH-PROOF-0',autoApprove:false,
    repos:['first','second'].map(name=>({url:process.env.PROOF_MODEL_URL+'/'+name+'.git',branch:'main'}))});
   id=created.id;assert(/^[a-z0-9]+$/i.test(id));const podName='session-'+id;
   const pod=()=>json('-n',ns,'get','pod',podName);
   const exec=script=>kubectl('-n',ns,'exec',podName,'-c','agent','--','/bin/sh','-ec',script);
   const starting=await api('sessions/'+id+'/provider-switch');assert.equal(starting.available,false);assert.match(starting.reason,/still starting/);
   await denied(id,'/preview',{requestId:crypto.randomUUID(),target:{agent:target,authMode:'ApiKey',credentialId:null,modelEndpointUrl:null,modelName:null},handover:{target,instructions:'Preserve the workspace and restrictions.',task:'Check startup readiness'}},/still starting/);
   terminal=await openTerminal(id);
   await until(async()=>{const page=await api('sessions/'+id+'/conversation?offset=0&max=500');return page.entries.some(entry=>entry.text.includes('Answer SWITCH-PROOF-0'));},'held source native answer');
   const first=await until(()=>{const value=pod();return value.status.phase==='Running'&&value.status.containerStatuses?.some(item=>item.name==='agent'&&item.ready)&&value;},'held source kubelet readiness');
   const owner=first.metadata.labels['agenthub.dev/owner'];
   const claim=first.spec.volumes.find(volume=>volume.name==='workspace').persistentVolumeClaim.claimName;
   const claimUid=json('-n',ns,'get','pvc',claim).metadata.uid;
   exec("for name in first second; do printf 'held dirty content\\n' >> /workspace/$name/tracked.txt; printf 'held untracked content\\n' > /workspace/$name/untracked.txt; done");
   const fingerprint=()=>kubectl('-n',ns,'exec',podName,'-c','agent','--','/bin/sh','-ec',fingerprintScript,'proof','/workspace');
   const baseline=fingerprint();
   const selection={requestId:crypto.randomUUID(),target:{agent:target,authMode:'ApiKey',credentialId:null,modelEndpointUrl:null,modelName:null},handover:{target,task:'Continue SWITCH-PROOF-1',instructions:'Keep the dirty repositories; earlier native context is omitted.'}};
   if(!subscriptions){seedSubscriptions(owner);subscriptions=true;}
   // A reviewed target can lose either API-key or account credentials before admission.
   for(const mode of ['ApiKey','Subscription']){
    const selected={...selection,requestId:crypto.randomUUID(),target:{...selection.target,authMode:mode,credentialId:mode==='Subscription'?'default':null}};
    const review=await api('sessions/'+id+'/provider-switch/preview','POST',selected);
    const secretName=mode==='ApiKey'?'creds-'+owner:target.toLowerCase()+'-'+owner;
    const stored=json('-n',ns,'get','secret',secretName);
    if(mode==='ApiKey'){
     const changed=structuredClone(stored);delete changed.data[target==='Codex'?'openai_api_key':'anthropic_api_key'];replace(changed);
    }else kubectl('-n',ns,'delete','secret',secretName,'--wait=true');
    try{
     await denied(id,'/preview',selected,mode==='ApiKey'?/target provider API key/:/usable target provider account/);
     await denied(id,'',{...selected,reviewHash:review.reviewHash},mode==='ApiKey'?/target provider API key/:/usable target provider account/);
     assert.equal(pod().metadata.uid,first.metadata.uid);assert.equal((await api('sessions/'+id)).executionGeneration,0);assert.equal(fingerprint(),baseline);
    }finally{
     if(mode==='ApiKey'){const current=json('-n',ns,'get','secret',secretName);current.data=stored.data;replace(current);}else create(stored);
    }
   }
   if(loss===false){
    const unknown={...selection,requestId:crypto.randomUUID(),target:{...selection.target,authMode:'CustomEndpoint',modelEndpointUrl:'https://model.example.com/v1',modelName:'unlisted-proof-model'}};
    await denied(id,'/preview',unknown,/catalog|capacity/i);
    if(target==='Claude'){
     const prior=await api('usage/limit');
     const reviewed=await api('sessions/'+id+'/provider-switch/preview','POST',selection);
     try{
      const limited=await api('usage/limit','PUT',{limitUsd:0});assert.equal(limited.blocked,true);
      await denied(id,'/preview',selection,/Monthly API budget reached/);
      await denied(id,'',{...selection,reviewHash:reviewed.reviewHash},/Monthly API budget reached/);
     }finally{await api('usage/limit','PUT',{limitUsd:prior.personalLimitUsd??null});}
    }
    assert.equal(pod().metadata.uid,first.metadata.uid);assert.equal((await api('sessions/'+id)).executionGeneration,0);assert.equal(fingerprint(),baseline);
    console.log(sourceAgent+': unknown endpoint model capacity'+(target==='Claude'?' and reached personal API budget':'')+' refused before source stop.');
   }
   // A stale registry UID must not authorize a different claim even with the same name.
   const stale=crypto.randomUUID();sql(`UPDATE session_workspaces SET claim_uid='${stale}' WHERE session_id='${id}';`);
   try{await denied(id,'/preview',selection,/workspace|claim/i);assert.equal(pod().metadata.uid,first.metadata.uid);assert.equal(fingerprint(),baseline);}
   finally{sql(`UPDATE session_workspaces SET claim_uid='${claimUid}' WHERE session_id='${id}';`);}
   console.log(sourceAgent+': missing/revoked API-key and subscription target credentials and stale claim UID refused before source stop.');
   if(loss==='source'){
    const checkpointId=crypto.randomUUID();
    const checkpointProgram=`const requestId=${JSON.stringify(checkpointId)};fetch("http://127.0.0.1:7681/agenthub/provider-switch",{method:"POST",headers:{"X-Agent-Token":process.env.AGENTHUB_CALLBACK_TOKEN,"Content-Type":"application/json"},body:JSON.stringify({requestId,action:"prepare"})}).then(async response=>process.stdout.write(JSON.stringify({status:response.status,body:await response.json()})))`;
    await until(()=>{const value=JSON.parse(kubectl('-n',ns,'exec',podName,'-c','agent','--','node','-e',checkpointProgram));return value.status===200&&value.body.checkpointed;},'source has an acknowledged native archive before pod loss');
    const cancelled=JSON.parse(kubectl('-n',ns,'exec',podName,'-c','agent','--','node','-e',`fetch("http://127.0.0.1:7681/agenthub/provider-switch",{method:"DELETE",headers:{"X-Agent-Token":process.env.AGENTHUB_CALLBACK_TOKEN,"Content-Type":"application/json"},body:JSON.stringify({requestId:${JSON.stringify(checkpointId)}})}).then(async response=>process.stdout.write(JSON.stringify({status:response.status})))`));assert.equal(cancelled.status,200);
    const shell=await openSourceShell(id);
    try{
     const review=await api('sessions/'+id+'/provider-switch/preview','POST',selection);
     await api('sessions/'+id+'/provider-switch','POST',{...selection,reviewHash:review.reviewHash});
     await until(async()=>{const value=await api('sessions/'+id+'/provider-switch');return value.operation?.progress.phase==='requested'&&value.operation.progress.error;},'source shell holds pre-stop operation');
     await api('sessions/'+id+'/messages','POST',{body:'HELD-SOURCE-'+sourceAgent,priority:true});
    }finally{await shell.close();}
    terminal.close();terminal=null;kubectl('-n',ns,'delete','pod',podName,'--grace-period=0','--wait=true');
    await api('sessions/'+id+'/provider-switch/retry','POST',{requestId:selection.requestId});
    const held=await until(async()=>{const value=await api('sessions/'+id+'/provider-switch');return value.operation?.progress.phase==='requested'&&value.operation.progress.error?.includes('original session runtime is unavailable')&&value;},'lost source stays durably held');
    assert.equal(held.operation.progress.sourceStopAcknowledged,false);
    assert((await api('sessions/'+id+'/messages')).some(message=>message.body==='HELD-SOURCE-'+sourceAgent&&message.deliveredAt==null));
    assert.equal((await api('sessions/'+id)).executionGeneration,0);assert.equal(json('-n',ns,'get','pvc',claim).metadata.uid,claimUid);
    const archive=await fetch(backendUrl+'/api/sessions/'+id+'/state',{headers});assert.equal(archive.status,200);assert((await archive.arrayBuffer()).byteLength>0);
    await inspectWorkspace(id,claim,baseline);
    await new Promise(resolve=>setTimeout(resolve,5000));assert.throws(()=>pod());
    console.log(sourceAgent+': source pod lost before stop stays held with generation/queue/native archive and byte-identical read-only workspace; no target is created.');
    await api('sessions/'+id,'DELETE',undefined,204);id=null;
    await until(()=>{try{json('-n',ns,'get','pvc',claim);return false;}catch{return true;}},'lost-source claim cleanup');
    continue;
   }
   if(loss==='schedule'){
    // Cordon only this disposable worker; existing source/backend pods keep running.
    const node=first.spec.nodeName;assert(node);assert.notEqual(json('get','node',node).spec.unschedulable,true);
    kubectl('cordon',node);cordonedNode=node;
    const review=await api('sessions/'+id+'/provider-switch/preview','POST',selection);
    const start={...selection,reviewHash:review.reviewHash};await api('sessions/'+id+'/provider-switch','POST',start);
    const queued='HELD-CHECKPOINT-'+sourceAgent+'-schedule';await api('sessions/'+id+'/messages','POST',{body:queued,priority:true});
    terminal.close();terminal=null;
    let retries=0;
    const pending=await until(async()=>{
     const state=await api('sessions/'+id+'/provider-switch');
     if(state.operation?.progress.phase==='requested'&&state.operation.progress.error?.startsWith('Finish the current turn')&&retries++<10)await api('sessions/'+id+'/provider-switch/retry','POST',{requestId:selection.requestId});
     try{const value=pod();return value.metadata.uid!==first.metadata.uid&&value.status.conditions?.some(item=>item.type==='PodScheduled'&&item.status==='False'&&item.reason==='Unschedulable')&&value;}catch{return false;}
    },'real scheduler holds target on cordoned worker');
    const duplicate=await api('sessions/'+id+'/provider-switch','POST',start);assert.equal(duplicate.request.requestId,selection.requestId);
    await denied(id,'',{...start,reviewHash:'changed-proof-review'},/different review/);
    await api('sessions/'+id+'/provider-switch/retry','POST',{requestId:selection.requestId});
    assert.equal(pod().metadata.uid,pending.metadata.uid);assert.equal(json('-n',ns,'get','pvc',claim).metadata.uid,claimUid);
    assert((await api('sessions/'+id+'/messages')).some(message=>message.body===queued&&message.deliveredAt==null));
    kubectl('uncordon',node);cordonedNode=null;
    await until(()=>{const value=pod();return value.status.containerStatuses?.some(item=>item.name==='agent'&&item.ready);},'same target becomes schedulable');
    terminal=await openTerminal(id);
    await until(async()=>{const state=await api('sessions/'+id+'/provider-switch');return state.operation?.progress.phase==='completed';},'scheduled target completes without a second admission');
    assert.equal(pod().metadata.uid,pending.metadata.uid);assert.equal(fingerprint(),baseline);
    const page=await api('sessions/'+id+'/conversation?offset=0&max=500');assert.equal(page.entries.filter(entry=>entry.generation===1&&entry.role==='user'&&entry.text===review.text).length,1);
    console.log(sourceAgent+'→'+target+': scheduler refusal retains target UID/claim/queue; duplicate request is idempotent, changed review refused, uncordon completes one handover.');
    terminal.close();terminal=null;await api('sessions/'+id,'DELETE',undefined,204);id=null;
    await until(()=>{try{json('-n',ns,'get','pvc',claim);return false;}catch{return true;}},'scheduler case claim cleanup');
    continue;
   }
   kubectl('-n',ns,'label','pod',podName,'provider-proof-storage=allowed','--overwrite');
   selector({matchLabels:{'agenthub.dev/component':'agent','provider-proof-storage':'allowed'}});
   const review=await api('sessions/'+id+'/provider-switch/preview','POST',selection);
   await api('sessions/'+id+'/provider-switch','POST',{...selection,reviewHash:review.reviewHash});
   const queued='HELD-CHECKPOINT-'+sourceAgent+'-'+loss;
   await api('sessions/'+id+'/messages','POST',{body:queued,priority:true});
   terminal.close();terminal=null;
   let retries=0;
   const targetPod=await until(async()=>{const current=await api('sessions/'+id+'/provider-switch'),progress=current.operation?.progress;if(progress?.phase==='requested'&&progress.error?.startsWith('Finish the current turn')&&retries++<10){await api('sessions/'+id+'/provider-switch/retry','POST',{requestId:selection.requestId});return false;}if(progress?.error&&progress.phase!=='target-started')throw Object.assign(new Error('Target start held unexpectedly: '+progress.error),{fatal:true});try{const value=pod();return value.metadata.uid!==first.metadata.uid&&value.status.containerStatuses?.some(item=>item.name==='agent'&&item.ready)&&value;}catch{return false;}},'held target starts');
   terminal=await openTerminal(id);
   const held=await until(async()=>{
    const value=await api('sessions/'+id+'/provider-switch'),progress=value.operation?.progress;
    if(progress?.phase==='requested'&&progress.error?.startsWith('Finish the current turn')&&retries++<10){await api('sessions/'+id+'/provider-switch/retry','POST',{requestId:selection.requestId});return false;}
    if(progress?.phase==='target-started'&&progress.error)return value;
    if(progress?.error)throw Object.assign(new Error('Unexpected held phase '+progress.phase+': '+progress.error),{fatal:true});return false;
   },'real denied state upload holds target before commit');
   const heldHistory=await api('sessions/'+id+'/conversation?offset=0&max=500');
   const sourceRows=heldHistory.entries.filter(entry=>entry.text.includes('SWITCH-PROOF-0'));
   assert(sourceRows.some(entry=>entry.role==='user'),'saved source conversation remains visible while target checkpoint is held');
   for(const entry of sourceRows){if(entry.generation!=null)assert.equal(entry.generation,0);if(entry.agent!=null)assert.equal(entry.agent,sourceAgent);}
   assert(!heldHistory.entries.some(entry=>entry.generation===0&&entry.role==='user'&&entry.text===review.text),'uncommitted target handover must not be labelled as the source generation');
   assert(held.operation.progress.sourceStopAcknowledged);assert.equal(pod().metadata.uid,targetPod.metadata.uid);assert.equal(fingerprint(),baseline);
   assert((await api('sessions/'+id+'/messages')).some(message=>message.body===queued&&message.deliveredAt===null));
   if(loss){
    terminal.close();terminal=null;kubectl('-n',ns,'delete','pod',podName,'--grace-period=0','--wait=true');
    selector(originalSelector);
    await api('sessions/'+id+'/provider-switch/retry','POST',{requestId:selection.requestId});
    await until(async()=>{const value=await api('sessions/'+id+'/provider-switch');return value.operation?.progress.phase==='target-started'&&value.operation.progress.error?.includes('uncertain start')&&value;},'lost target remains held without replay');
    await denied(id,'/cancel',{requestId:selection.requestId},/Source stop has begun/);
    const retained=await api('sessions/'+id+'/conversation?offset=0&max=500');assert(retained.entries.some(entry=>entry.role==='user'&&entry.text.includes('SWITCH-PROOF-0')));
    await new Promise(resolve=>setTimeout(resolve,5000));assert.throws(()=>pod());
    assert.equal(json('-n',ns,'get','pvc',claim).metadata.uid,claimUid);
    await inspectWorkspace(id,claim,baseline);
    assert((await api('sessions/'+id+'/messages')).some(message=>message.body===queued&&message.deliveredAt===null));
    console.log(sourceAgent+'→'+target+': failed actual archive upload then lost target stays held, no pod recreation/handover replay; same claim and queue retained.');
   }else{
    selector(originalSelector);await api('sessions/'+id+'/provider-switch/retry','POST',{requestId:selection.requestId});
    await until(async()=>{const value=await api('sessions/'+id+'/provider-switch');return value.operation?.progress.phase==='completed';},'restored state egress completes the same replacement');
    assert.equal(pod().metadata.uid,targetPod.metadata.uid);assert.equal(fingerprint(),baseline);assert.equal(json('-n',ns,'get','pvc',claim).metadata.uid,claimUid);
    const page=await api('sessions/'+id+'/conversation?offset=0&max=500');assert.equal(page.entries.filter(entry=>entry.generation===1&&entry.role==='user'&&entry.text===review.text).length,1);
    await until(()=>{const rows=JSON.parse(kubectl('-n',ns,'exec',podName,'-c','agent','--','node','-e',`fetch(${JSON.stringify(process.env.PROOF_MODEL_URL+'/proof/requests')}).then(response=>response.json()).then(value=>process.stdout.write(JSON.stringify(value)))`));return rows.some(row=>row.agent===target.toLowerCase()&&row.heldMarkers?.includes(queued));},'held message reaches actual model only after checkpoint recovery');
    console.log(sourceAgent+'→'+target+': real checkpoint egress failure held before commit, restored storage + owner retry completes same target/native handover and dirty workspace.');
   }
   terminal?.close();terminal=null;await api('sessions/'+id,'DELETE',undefined,204);id=null;
   await until(()=>{try{json('-n',ns,'get','pvc',claim);return false;}catch{return true;}},'held case claim cleanup');
  }
 }catch(error){
  console.error('Held recovery failed:',error.message);
  if(id){try{console.error('Held operation:',JSON.stringify(await api('sessions/'+id+'/provider-switch')));}catch{}try{console.error(kubectl('-n',ns,'logs','session-'+id,'-c','agent','--tail=80'));}catch{}}
  throw error;
 }finally{
  if(cordonedNode)kubectl('uncordon',cordonedNode);
  selector(originalSelector);terminal?.close();if(id){try{await api('sessions/'+id,'DELETE',undefined,204);}catch{console.error('Held proof cleanup retained session for diagnostics.');}}
 }
}
