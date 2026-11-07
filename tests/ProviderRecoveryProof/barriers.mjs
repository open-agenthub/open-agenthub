// Exercise the external crash barrier on real PostgreSQL before using it on a kubelet rig.
import assert from 'node:assert/strict';
import fs from 'node:fs';
import crypto from 'node:crypto';
import {execFileSync,spawn} from 'node:child_process';
const psql=process.env.PROOF_PSQL||'psql';
const base=process.env.PROOF_POSTGRES||'postgresql://postgres@127.0.0.1:54329/postgres';
const address=new URL(base);assert(['localhost','127.0.0.1'].includes(address.hostname));
const name='proof529_'+crypto.randomBytes(6).toString('hex');
const url=new URL(base);url.pathname='/'+name;
const sql=(connection,query)=>execFileSync(psql,['-X','-A','-t','-v','ON_ERROR_STOP=1',connection],{input:query,encoding:'utf8',stdio:['pipe','pipe','pipe']}).trim();
const sleep=ms=>new Promise(resolve=>setTimeout(resolve,ms));
const request='11111111-1111-4111-8111-111111111111';
let worker;
try{
 sql(base,'CREATE DATABASE '+name+';');
 sql(url.toString(),`CREATE TABLE session_provider_switches(request_id UUID,phase TEXT,progress JSONB); INSERT INTO session_provider_switches VALUES('${request}','requested','{}');`+fs.readFileSync(new URL('./gates.sql',import.meta.url),'utf8')+`INSERT INTO provider_recovery_proof_gates VALUES('${request}',1,'prepared',NULL,FALSE);`);
 worker=spawn(psql,['-X','-A','-t','-v','ON_ERROR_STOP=1',url.toString()],{stdio:['pipe','pipe','pipe']});
 worker.stdin.end("UPDATE session_provider_switches SET phase='prepared';");
 let pid;
 for(let attempt=0;attempt<100&&!pid;attempt++){
  pid=sql(url.toString(),"SELECT pid FROM pg_locks WHERE locktype='advisory' AND classid=529 AND objid=1 AND granted;");
  if(!pid)await sleep(50);
 }
 assert(pid,'external barrier must become observable');
 assert.equal(sql(url.toString(),'SELECT phase FROM session_provider_switches;'),'requested');
 const exited=new Promise(resolve=>worker.once('exit',resolve));
 assert.equal(sql(url.toString(),'SELECT pg_terminate_backend('+Number(pid)+');'),'t');
 assert.notEqual(await exited,0);
 assert.equal(sql(url.toString(),'SELECT phase FROM session_provider_switches;'),'requested');
 sql(url.toString(),"UPDATE provider_recovery_proof_gates SET released=TRUE; UPDATE session_provider_switches SET phase='prepared';");
 assert.equal(sql(url.toString(),'SELECT phase FROM session_provider_switches;'),'prepared');
 sql(url.toString(),`INSERT INTO provider_recovery_proof_gates VALUES('${request}',2,'stopping',NULL,FALSE);`);
 worker=spawn(psql,['-X','-A','-t','-v','ON_ERROR_STOP=1',url.toString()],{stdio:['pipe','pipe','pipe']});
 const released=new Promise(resolve=>worker.once('exit',resolve));
 worker.stdin.end("UPDATE session_provider_switches SET phase='stopping';");
 let second;
 for(let attempt=0;attempt<100&&!second;attempt++){
  second=sql(url.toString(),"SELECT pid FROM pg_locks WHERE locktype='advisory' AND classid=529 AND objid=2 AND granted;");
  if(!second)await sleep(50);
 }
 assert(second,'live barrier must become observable');
 assert.equal(sql(url.toString(),'SELECT phase FROM session_provider_switches;'),'prepared');
 sql(url.toString(),'UPDATE provider_recovery_proof_gates SET released=TRUE WHERE sequence=2;');
 assert.equal(await released,0,'the live barrier must observe external release');
 assert.equal(sql(url.toString(),'SELECT phase FROM session_provider_switches;'),'stopping');
 console.log('PASS: real PostgreSQL barrier exposes the prior committed phase; backend termination rolls back only the blocked write; release permits one continuation.');
}finally{
 if(worker?.exitCode===null)worker.kill('SIGTERM');
 sql(base,'DROP DATABASE IF EXISTS '+name+' WITH (FORCE);');
}
