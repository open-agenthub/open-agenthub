import assert from 'node:assert/strict';
import fs from 'node:fs';
import {execFileSync} from 'node:child_process';
const sleep=ms=>new Promise(resolve=>setTimeout(resolve,ms));
const phases=[['prepared',null],['stopping',false],['stopping',true],['source-stopped',null],['target-admitted',null],['target-starting',null],['target-started',null],['committing',null],['releasing',null],['completed',null]];
export function createRecoveryFaults({json,control='agenthub'}){
 assert.equal(process.env.GITHUB_EVENT_NAME,'workflow_dispatch','faults require the explicit disposable-worker workflow');
 const sql=query=>execFileSync('kubectl',['-n',control,'exec','-i','postgres-0','-c','postgres','--','psql','-X','-A','-t','-v','ON_ERROR_STOP=1','-U','agenthub','-d','agenthub'],{input:query,encoding:'utf8',stdio:['pipe','pipe','pipe']}).trim();
 const cleanupSql='UPDATE provider_recovery_proof_gates SET released=TRUE; DROP TRIGGER provider_recovery_proof_gate ON session_provider_switches; DROP FUNCTION provider_recovery_proof_barrier(); DROP TABLE provider_recovery_proof_gates;';
 sql(fs.readFileSync(new URL('./gates.sql',import.meta.url),'utf8'));
 let next=0,failure,stopping=false;
 const operations=new Map();
 const check=()=>{if(failure)throw Object.assign(failure,{fatal:true});};
 async function wait(fn,description){
  for(let attempt=0;attempt<360;attempt++){
   if(stopping)throw new Error('fault coordinator stopped');
   const value=fn();if(value)return value;await sleep(250);
  }
  throw new Error(description+': timeout');
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
    const before=sql(`SELECT phase FROM session_provider_switches WHERE request_id='${requestId}';`);
    const identity=sql(`SELECT md5(json_build_array(s.agent,s.execution_generation,s.callback_token)::text) FROM sessions s JOIN session_provider_switches p ON p.session_id=s.id WHERE p.request_id='${requestId}';`);
    const ip=sql(`SELECT host(client_addr) FROM pg_stat_activity WHERE pid=${Number(pid)};`);
    const backend=json('-n',control,'get','pods','-l','app=agenthub-backend').items.find(pod=>pod.status?.podIP===ip);
    assert(backend,'the blocked database writer must be a real backend pod');
    execFileSync('kubectl',['-n',control,'delete','pod',backend.metadata.name,'--grace-period=0','--wait=true','--timeout=90s'],{stdio:'pipe'});
    await wait(()=>sql('SELECT count(*) FROM pg_stat_activity WHERE pid='+Number(pid)+';')==='0','crashed backend connection disappears');
    assert.equal(sql(`SELECT phase FROM session_provider_switches WHERE request_id='${requestId}';`),before,'crash rolls back the unacknowledged phase write');
    assert.equal(sql(`SELECT md5(json_build_array(s.agent,s.execution_generation,s.callback_token)::text) FROM sessions s JOIN session_provider_switches p ON p.session_id=s.id WHERE p.request_id='${requestId}';`),identity,'generation/provider/authority rotation must roll back with the interrupted phase');
    sql('UPDATE provider_recovery_proof_gates SET released=TRUE WHERE sequence='+gate.sequence+';');
    execFileSync('kubectl',['-n',control,'rollout','status','deployment/agenthub-backend','--timeout=120s'],{stdio:'pipe'});
    console.log('Recovery '+requestId+': crashed real backend at '+before+' → '+gate.phase+'; committed phase retained.');
   }
  })().catch(error=>{failure=error;});
  operations.set(requestId,completion);
 }
 return {
  check,arm,
  async finish(requestId){await operations.get(requestId);check();},
  async close(){stopping=true;sql(cleanupSql);await Promise.all(operations.values());},
 };
}
