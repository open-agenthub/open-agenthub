import fs from 'node:fs';

// Match only an isolated server's direct PTY child, never its credential watcher or exec probe.
export function killNativeChild(parentPid,provider) {
 if(!['claude','codex'].includes(provider))throw new Error('Unsupported native fault provider.');
 const children=fs.readFileSync(`/proc/${parentPid}/task/${parentPid}/children`,'utf8').trim().split(/\s+/).filter(Boolean).map(Number);
 const matches=children.filter(pid=>{
  try{return fs.readFileSync(`/proc/${pid}/cmdline`,'utf8').split('\0').slice(0,2).some(arg=>[provider,provider+'-native'].includes(arg.split('/').at(-1)));}
  catch{return false;}
 });
 if(matches.length!==1)throw new Error('Native fault requires exactly one matching direct PTY child.');
 process.kill(matches[0],'SIGKILL');return matches[0];
}
