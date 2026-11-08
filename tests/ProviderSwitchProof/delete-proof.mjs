import assert from 'node:assert/strict';
import http from 'node:http';
import { deleteProofSession } from './delete.mjs';
for(const loseAfterDelete of [true,false]){
 let calls=0,removed=false,reads=0;
 const server=http.createServer((request,response)=>{
  assert.equal(request.headers.connection,'close');
  if(request.method==='DELETE'){
   calls++;
   if(calls===1){removed=loseAfterDelete;request.socket.destroy();return;}
   removed=true;response.writeHead(204).end();return;
  }
  reads++;response.writeHead(removed?404:200).end();
 });
 await new Promise(resolve=>server.listen(0,'127.0.0.1',resolve));
 try{
  await deleteProofSession('http://127.0.0.1:'+server.address().port,'synthetic-proof-session',{}, {wait:async()=>{}});
  assert(removed);assert.equal(reads,1);assert.equal(calls,loseAfterDelete?1:2);
 }finally{server.closeAllConnections();await new Promise(resolve=>server.close(resolve));}
}
console.log('PASS: real HTTP lost DELETE reply reconciles already-deleted state or retries the same existing resource; fresh connections, no native input replay.');

const sockets = new Set();
const fresh = http.createServer((request,response)=>{sockets.add(request.socket);response.end('{}');});
await new Promise(resolve=>fresh.listen(0,'127.0.0.1',resolve));
try{
 for(let index=0;index<4;index++){
  const response=await fetch('http://127.0.0.1:'+fresh.address().port,{method:index===3?'POST':'GET',headers:{Connection:'close'}});
  await response.arrayBuffer();
 }
 assert.equal(sockets.size,4,'owner requests must not reuse a retiring backend connection');
}finally{fresh.closeAllConnections();await new Promise(resolve=>fresh.close(resolve));}
console.log('PASS: real fetch uses a new connection for each owner request when Connection: close is set.');
