// Exercise the external crash barrier with the production split between lifecycle and journal connections.
import assert from 'node:assert/strict';
import fs from 'node:fs';
import crypto from 'node:crypto';
import {execFileSync,spawn} from 'node:child_process';
const psql=process.env.PROOF_PSQL||'psql';
const base=process.env.PROOF_POSTGRES||'postgresql://postgres@127.0.0.1:54329/postgres';
const address=new URL(base);assert(['localhost','127.0.0.1'].includes(address.hostname));
const name='proof529_'+crypto.randomBytes(6).toString('hex');
const url=new URL(base);url.pathname='/'+name;
const sql=(connection,query)=>execFileSync(psql,['-q','-X','-A','-t','-v','ON_ERROR_STOP=1',connection,'-c',query],{encoding:'utf8',stdio:['ignore','pipe','pipe'],timeout:10000}).trim();
const sleep=ms=>new Promise(resolve=>setTimeout(resolve,ms));
const request='11111111-1111-4111-8111-111111111111';
const clients=[];
function client(query,keepOpen=false){
 const child=spawn(psql,['-q','-X','-A','-t','-v','ON_ERROR_STOP=1',url.toString()],{stdio:['pipe','pipe','pipe']});
 child.stdout.resume();child.stderr.resume();child.done=new Promise(resolve=>child.once('exit',resolve));clients.push(child);
 if(keepOpen)child.stdin.write(query+'\n');else child.stdin.end(query);return child;
}
async function wait(query,expected){for(let n=0;n<200;n++){const value=sql(url.toString(),query);if(value===expected)return value;await sleep(50);}throw new Error('database condition timed out: '+query);}
try{
 sql(base,'CREATE DATABASE '+name+';');
 sql(url.toString(),`CREATE TABLE session_provider_switches(request_id UUID,phase TEXT,progress JSONB); INSERT INTO session_provider_switches VALUES('${request}','requested','{}');`+fs.readFileSync(new URL('./gates.sql',import.meta.url),'utf8')+`INSERT INTO provider_recovery_proof_gates(request_id,sequence,phase) VALUES('${request}',1,'prepared');`);
 // The real lifecycle lock uses a separate pool, so its idle connection observes client
 // death even while the journal's artificial long-running statement is still executing.
 const lifecycle=client("SET application_name='proof529-lifecycle'; SELECT pg_advisory_lock(hashtextextended('proof529-session',1));",true);
 await wait("SELECT EXISTS(SELECT 1 FROM pg_locks l JOIN pg_stat_activity a ON a.pid=l.pid WHERE a.application_name='proof529-lifecycle' AND l.locktype='advisory' AND l.granted);",'t');
 const writer=client("UPDATE session_provider_switches SET phase='prepared';");
 await wait("SELECT count(*) FROM pg_locks WHERE locktype='advisory' AND classid=529 AND objid=1 AND granted;",'1');
 const handoff=client("SET application_name='proof529-handoff'; SELECT pg_advisory_lock(hashtextextended('proof529-session',1)); DO $$ BEGIN WHILE NOT EXISTS(SELECT 1 FROM provider_recovery_proof_gates WHERE sequence=1 AND handoff_released) LOOP PERFORM pg_sleep(0.1); END LOOP; END $$; SELECT pg_advisory_unlock(hashtextextended('proof529-session',1));");
 await wait("SELECT count(*) FROM pg_locks l JOIN pg_stat_activity a ON a.pid=l.pid WHERE a.application_name='proof529-handoff' AND l.locktype='advisory' AND NOT l.granted;",'1');
 const startup=client('ALTER TABLE session_provider_switches ADD COLUMN proof_initialized BOOLEAN;');
 await wait("SELECT count(*) FROM pg_locks WHERE relation='session_provider_switches'::regclass AND mode='AccessExclusiveLock' AND NOT granted;",'1');
 assert.throws(()=>sql(url.toString(),"SET statement_timeout='300ms'; SELECT phase FROM session_provider_switches;"),/timeout/);
 lifecycle.kill('SIGKILL');writer.kill('SIGKILL');await lifecycle.done;await writer.done;
 await wait("SELECT count(*) FROM pg_locks l JOIN pg_stat_activity a ON a.pid=l.pid WHERE a.application_name='proof529-handoff' AND l.locktype='advisory' AND l.granted;",'1');
 // No PostgreSQL signal is needed: roll back only the now-orphaned proof statement.
 sql(url.toString(),'UPDATE provider_recovery_proof_gates SET aborted=TRUE WHERE sequence=1;');
 assert.equal(await startup.done,0,'schema startup finishes while only the lifecycle advisory lease is parked');
 assert.equal(sql(url.toString(),'SELECT phase FROM session_provider_switches;'),'requested','the synthetic crash boundary rolls back');
 assert.equal(sql(url.toString(),"SELECT pg_try_advisory_lock(hashtextextended('proof529-session',1));"),'f');
 sql(url.toString(),'UPDATE provider_recovery_proof_gates SET released=TRUE,handoff_released=TRUE WHERE sequence=1;');
 assert.equal(await handoff.done,0,'lease client exits through SQL release, without database signals');
 assert.equal(sql(url.toString(),"SELECT pg_try_advisory_lock(hashtextextended('proof529-session',1));"),'t');
 sql(url.toString(),"UPDATE session_provider_switches SET phase='prepared';");
 sql(url.toString(),`INSERT INTO provider_recovery_proof_gates(request_id,sequence,phase) VALUES('${request}',2,'stopping');`);
 const live=client("UPDATE session_provider_switches SET phase='stopping';");
 await wait("SELECT count(*) FROM pg_locks WHERE locktype='advisory' AND classid=529 AND objid=2 AND granted;",'1');
 assert.equal(sql(url.toString(),'SELECT phase FROM session_provider_switches;'),'prepared');
 sql(url.toString(),'UPDATE provider_recovery_proof_gates SET released=TRUE WHERE sequence=2;');
 assert.equal(await live.done,0);assert.equal(sql(url.toString(),'SELECT phase FROM session_provider_switches;'),'stopping');
 console.log('PASS: separate lifecycle/journal clients die; SQL abort rolls back the orphaned proof writer; schema initializes under advisory handoff; SQL lease release and live gate continuation need no database signals.');
}finally{
 for(const child of clients)if(child.exitCode===null&&child.signalCode===null)child.kill('SIGTERM');
 sql(base,'DROP DATABASE IF EXISTS '+name+' WITH (FORCE);');
}
