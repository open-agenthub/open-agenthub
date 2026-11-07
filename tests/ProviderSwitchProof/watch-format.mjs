import assert from 'node:assert/strict';import http from 'node:http';import {spawn} from 'node:child_process';
const pod=(uid,version)=>({apiVersion:'v1',kind:'Pod',metadata:{uid,name:uid,namespace:'proof',resourceVersion:version}});
const s=http.createServer((req,res)=>{const u=new URL(req.url,'http://example.com');res.setHeader('content-type','application/json');
if(u.pathname==='/api')return res.end(JSON.stringify({kind:'APIVersions',apiVersion:'v1',versions:['v1']}));
if(u.pathname==='/apis')return res.end('{"kind":"APIGroupList","apiVersion":"v1","groups":[]}');
if(u.pathname==='/api/v1')return res.end(JSON.stringify({kind:'APIResourceList',apiVersion:'v1',groupVersion:'v1',resources:[{name:'pods',singularName:'pod',namespaced:true,kind:'Pod',verbs:['get','list','watch']}]}));
if(u.searchParams.get('watch')==='true'){res.write(JSON.stringify({type:'DELETED',object:pod('source','2')})+'\n');res.write(JSON.stringify({type:'ADDED',object:pod('target','3')})+'\n');return res.end();}
res.end(JSON.stringify({kind:'PodList',apiVersion:'v1',metadata:{resourceVersion:'1'},items:[pod('source','1')]}));});
await new Promise(r=>s.listen(0,'127.0.0.1',r));
const child=spawn(process.env.PROOF_KUBECTL||'kubectl',['--server=http://127.0.0.1:'+s.address().port,'-n','proof','get','pods','--watch','--output-watch-events','-o','jsonpath={.type}{"\\t"}{.object.metadata.uid}{"\\n"}']);
let output='',err='';child.stdout.on('data',b=>output+=b);child.stderr.on('data',b=>err+=b);
setTimeout(()=>child.kill('SIGTERM'),1500);await new Promise(r=>child.once('exit',r));s.close();assert.equal(err,'');assert.deepEqual(output.trim().split('\n').map(line=>line.trim().split(/\s+/)),[['ADDED','source'],['DELETED','source'],['ADDED','target']]);console.log('PASS: actual kubectl expands watch tabs; whitespace framing retains initial/add/delete identities.');
