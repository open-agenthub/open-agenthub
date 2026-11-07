import assert from 'node:assert/strict';
import fs from 'node:fs';
import {execFileSync,spawn} from 'node:child_process';
const sleep=ms=>new Promise(resolve=>setTimeout(resolve,ms));
const phases=[['prepared',null],['stopping',false],['stopping',true],['source-stopped',null],['target-admitted',null],['target-starting',null],['target-started',null],['committing',null],['releasing',null],['completed',null]];
export function createRecoveryFaults({json,control='agenthub'}){
 assert.equal(process.env.GITHUB_EVENT_NAME,'workflow_dispatch','faults require the explicit disposable-worker workflow');
 const psql=['-n',control,'exec','postgres-0','-c','postgres','--','psql','-q','-X','-A','-t','-v','ON_ERROR_STOP=1','-U','agenthub','-d','agenthub','-c'];
 const sql=query=>execFileSync('kubectl',[...psql,"SET statement_timeout='15s'; SET lock_timeout='10s'; "+query],{encoding:'utf8',stdio:['ignore','pipe','pipe'],timeout:20000}).trim();
 sql(fs.readFileSync(new URL('./gates.sql',import.meta.url),'utf8'));
 let next=0,failure,stopping=false;
 const operations=new Map(),leases=new Set();
 const check=()=>{if(failure)throw Object.assign(failure,{fatal:true});};
 async function wait(fn,description){
  for(let attempt=0;attempt<360;attempt++){
   if(stopping)throw new Error('fault coordinator stopped');
   const value=fn();if(value)return value;await sleep(250);
  }
  throw new Error(description+': timeout');
 }
 async function park(id,sequence){
  assert(/^[a-z0-9-]{1,64}$/i.test(id));
  const name='provider_recovery_handoff_'+sequence;
  // Queue before the crash, ahead of replicas trying the nonblocking lifecycle lease.
  // The lease holds no journal/table lock while a replacement runs schema initialization.
  const child=spawn('kubectl',[...psql,`SET application_name='${name}'; SELECT pg_advisory_lock(hashtextextended('${id}',1)); SELECT pg_sleep(600);`],{stdio:['ignore','pipe','pipe']});
  child.stdout.resume();child.stderr.resume();
  const lease={child,name};leases.add(lease);
  lease.pid=await wait(()=>sql(`SELECT a.pid FROM pg_stat_activity a JOIN pg_locks l ON l.pid=a.pid WHERE a.application_name='${name}' AND l.locktype='advisory' AND NOT l.granted;`),'external lifecycle waiter queues');
  assert(/^\d+$/.test(lease.pid));
  return lease;
 }
 async function unpark(lease){
  const exited=new Promise(resolve=>{if(lease.child.exitCode!==null)resolve();else lease.child.once('exit',resolve);});
  // The connection can queue successfully before its PID reaches the observer; cleanup
  // still needs to release that remote lease if observation itself fails.
  sql(`SELECT pg_terminate_backend(pid) FROM pg_stat_activity WHERE application_name='${lease.name}';`);
  let timer;
  try { await Promise.race([exited,new Promise((_,reject)=>{timer=setTimeout(()=>reject(new Error('external lifecycle client did not exit')),10000);})]); }
  finally { clearTimeout(timer); }
  leases.delete(lease);
 }
 async function arm(id,requestId,cancellation=false){
  assert(/^[0-9a-f-]{36}$/i.test(requestId));
  const plan=cancellation?[['cancelling',null],['cancelled',null]]:phases;
  const gates=plan.map(([phase,ack])=>({sequence:++next,phase,ack}));
  sql('INSERT INTO provider_recovery_proof_gates(request_id,sequence,phase,stop_ack) VALUES '+gates.map(gate=>`('${requestId}',${gate.sequence},'${gate.phase}',${gate.ack===null?'NULL':gate.ack?'TRUE':'FALSE'})`).join(',')+';');
  const completion=(async()=>{
   for(const gate of gates){
    const pid=await wait(()=>sql(`SELECT pid FROM pg_locks WHERE locktype='advisory' AND classid=529 AND objid=${gate.sequence} AND granted;`),'phase barrier '+gate.phase);
    assert(/^\d+$/.test(pid),'one worker must hold the external barrier');
    console.log('Recovery '+requestId+': observed external '+gate.phase+' barrier.');
    const before=sql(`SELECT phase FROM session_provider_switches WHERE request_id='${requestId}';`);
    const snapshot=()=>sql(`SELECT md5(json_build_array(s.agent,s.execution_generation,s.callback_token)::text) FROM sessions s JOIN session_provider_switches p ON p.session_id=s.id WHERE p.request_id='${requestId}';`);
    const identity=snapshot();
    const ip=sql(`SELECT host(client_addr) FROM pg_stat_activity WHERE pid=${Number(pid)};`).replace(/^::ffff:/,'');
    const backend=json('-n',control,'get','pods','-l','app=agenthub-backend').items.find(pod=>pod.status?.podIP===ip);
    assert(backend,'the blocked database writer must be a real backend pod');
    const lease=await park(id,gate.sequence);
    execFileSync('kubectl',['-n',control,'delete','pod',backend.metadata.name,'--grace-period=0','--wait=true','--timeout=90s'],{stdio:'pipe',timeout:100000});
    await wait(()=>sql('SELECT count(*) FROM pg_stat_activity WHERE pid='+Number(pid)+';')==='0','crashed backend connection disappears');
    await wait(()=>sql(`SELECT EXISTS(SELECT 1 FROM pg_locks WHERE pid=${Number(lease.pid)} AND locktype='advisory' AND granted);`)==='t','external lifecycle lease acquired');
    assert.equal(sql(`SELECT phase FROM session_provider_switches WHERE request_id='${requestId}';`),before,'crash rolls back the unacknowledged phase write');
    assert.equal(snapshot(),identity,'generation/provider/authority rotation must roll back with the interrupted phase');
    sql('UPDATE provider_recovery_proof_gates SET released=TRUE WHERE sequence='+gate.sequence+';');
    execFileSync('kubectl',['-n',control,'rollout','status','deployment/agenthub-backend','--timeout=120s'],{stdio:'pipe',timeout:130000});
    await unpark(lease);
    console.log('Recovery '+requestId+': crashed real backend at '+before+' → '+gate.phase+'; committed phase retained.');
   }
  })().catch(error=>{if(!stopping){failure=error;console.error('Fault coordinator failed:',error.message);}});
  operations.set(requestId,completion);
 }
 return {
  check,arm,
  async finish(requestId){await operations.get(requestId);check();},
  async close(){
   stopping=true;
   // Release commits separately: combining this UPDATE with DROP TRIGGER in one -c batch
   // would wait for a writer that cannot observe the uncommitted gate release.
   sql('UPDATE provider_recovery_proof_gates SET released=TRUE;');
   for(const lease of [...leases])await unpark(lease);
   await Promise.all(operations.values());
   sql('DROP TRIGGER provider_recovery_proof_gate ON session_provider_switches; DROP FUNCTION provider_recovery_proof_barrier(); DROP TABLE provider_recovery_proof_gates;');
  },
 };
}
