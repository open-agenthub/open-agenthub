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
 async function wait(fn,description,cleanup=false){
  for(let attempt=0;attempt<360;attempt++){
   if(stopping&&!cleanup)throw new Error('fault coordinator stopped');
   const value=fn();if(value)return value;await sleep(250);
  }
  throw new Error(description+': timeout');
 }
 async function park(id,sequence){
  assert(/^[a-z0-9-]{1,64}$/i.test(id));
  const name='provider_recovery_handoff_'+sequence;
  // Poll before the crash: blocking waiters may never wake under host signal confinement.
  // The lease holds no application table lock while a replacement runs schema initialization.
  const child=spawn('kubectl',[...psql,`SET application_name='${name}'; DO $$ BEGIN WHILE NOT pg_try_advisory_lock(hashtextextended('${id}',1)) LOOP PERFORM pg_sleep(0.001); END LOOP; WHILE NOT EXISTS(SELECT 1 FROM provider_recovery_proof_gates WHERE sequence=${sequence} AND handoff_released) LOOP PERFORM pg_sleep(0.1); END LOOP; END $$; SELECT pg_advisory_unlock(hashtextextended('${id}',1));`],{stdio:['ignore','pipe','pipe']});
  child.stdout.resume();let stderr='';child.stderr.on('data',bytes=>{stderr+=bytes;});
  child.on('error',error=>{failure=error;});
  child.once('exit',(code,signal)=>console.log('Recovery lease '+name+': client exit '+code+'/'+signal+(stderr?' ('+stderr.trim()+')':'')));
  const lease={child,name,sequence};leases.add(lease);
  lease.pid=await wait(()=>sql(`SELECT pid FROM pg_stat_activity WHERE application_name='${name}' AND state='active';`),'external lifecycle polling client starts');
  assert(/^\d+$/.test(lease.pid));
  console.log('Recovery lease '+name+': polling database PID '+lease.pid);
  return lease;
 }
 async function unpark(lease){
  const exited=new Promise(resolve=>{if(lease.child.exitCode!==null||lease.child.signalCode!==null)resolve();else lease.child.once('exit',resolve);});
  // The connection can queue successfully before its PID reaches the observer; cleanup
  // still needs to release that remote lease if observation itself fails.
  sql('UPDATE provider_recovery_proof_gates SET handoff_released=TRUE WHERE sequence='+lease.sequence+';');
  await wait(()=>sql(`SELECT count(*) FROM pg_stat_activity WHERE application_name='${lease.name}';`)==='0','external lifecycle database connection disappears',true);
  let timer;
  try { await Promise.race([exited,new Promise(resolve=>{timer=setTimeout(resolve,2000);})]); }
  finally { clearTimeout(timer); }
  if(lease.child.exitCode===null&&lease.child.signalCode===null){
   console.warn('Recovery lease '+lease.name+': database lease released; terminate remaining local exec transport');
   lease.child.kill('SIGTERM');
  }
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
    console.log('Recovery '+requestId+': deleted backend '+backend.metadata.name+' for writer PID '+pid);
    await wait(()=>sql(`SELECT EXISTS(SELECT 1 FROM pg_locks WHERE pid=${Number(lease.pid)} AND locktype='advisory' AND granted);`)==='t','external lifecycle lease acquired');
    // A disconnected client's deliberately unbounded query may keep running in PostgreSQL.
    // Abort only this synthetic barrier after lifecycle ownership proves the backend died;
    // host confinement can deny pg_terminate_backend signals even for the same DB role.
    sql('UPDATE provider_recovery_proof_gates SET aborted=TRUE WHERE sequence='+gate.sequence+';');
    await wait(()=>sql('SELECT count(*) FROM pg_stat_activity WHERE pid='+Number(pid)+';')==='0','crashed backend connection disappears');
    console.log('Recovery '+requestId+': synthetic writer aborted after backend death');
    console.log('Recovery '+requestId+': external lifecycle lease acquired');
    assert.equal(sql(`SELECT phase FROM session_provider_switches WHERE request_id='${requestId}';`),before,'crash rolls back the unacknowledged phase write');
    assert.equal(snapshot(),identity,'generation/provider/authority rotation must roll back with the interrupted phase');
    sql('UPDATE provider_recovery_proof_gates SET released=TRUE WHERE sequence='+gate.sequence+';');
    console.log('Recovery '+requestId+': gate released, waiting for replacement startup');
    execFileSync('kubectl',['-n',control,'rollout','status','deployment/agenthub-backend','--timeout=120s'],{stdio:'pipe',timeout:130000});
    await unpark(lease);
    console.log('Recovery '+requestId+': crashed real backend at '+before+' → '+gate.phase+'; committed phase retained.');
   }
  })().catch(error=>{if(!stopping){failure=error;console.error('Fault coordinator failed:',error.message);try{console.error('Recovery database waits:',sql("SELECT json_agg(json_build_object('pid',pid,'application',application_name,'state',state,'waitType',wait_event_type,'wait',wait_event,'blockedBy',pg_blocking_pids(pid))) FROM pg_stat_activity WHERE datname=current_database() AND pid<>pg_backend_pid();"));}catch{}}});
  operations.set(requestId,completion);
 }
 return {
  check,arm,
  diagnostics(){
   try{console.error('Recovery database waits:',sql("SELECT json_agg(json_build_object('pid',pid,'application',application_name,'state',state,'waitType',wait_event_type,'wait',wait_event,'blockedBy',pg_blocking_pids(pid))) FROM pg_stat_activity WHERE datname=current_database() AND pid<>pg_backend_pid();"));}catch(error){console.error('Recovery wait diagnostics unavailable:',error.message);}
  },
  async finish(requestId){await operations.get(requestId);check();},
  async close(){
   stopping=true;
   // Release commits separately: combining this UPDATE with DROP TRIGGER in one -c batch
   // would wait for a writer that cannot observe the uncommitted gate release.
   sql('UPDATE provider_recovery_proof_gates SET aborted=TRUE,released=TRUE,handoff_released=TRUE;');
   for(const lease of [...leases])await unpark(lease);
   await Promise.all(operations.values());
   sql('DROP TRIGGER provider_recovery_proof_gate ON session_provider_switches; DROP FUNCTION provider_recovery_proof_barrier(); DROP TABLE provider_recovery_proof_gates;');
  },
 };
}
