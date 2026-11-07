import http from 'node:http';
import fs from 'node:fs';
import path from 'node:path';
import crypto from 'node:crypto';
import { execFileSync } from 'node:child_process';
import { gunzipSync } from 'node:zlib';
const root = fs.mkdtempSync('/tmp/provider-switch-model-');
for (const name of ['first', 'second']) {
 const work = path.join(root, name), bare = path.join(root, name + '.git');
 fs.mkdirSync(work);
 const git = args => execFileSync('git', args, { cwd: work, stdio: 'pipe' });
 git(['init', '-q', '-b', 'main']); git(['config', 'user.name', 'Proof']); git(['config', 'user.email', 'proof@example.com']);
 fs.writeFileSync(path.join(work, 'tracked.txt'), name + ' baseline\n');
 git(['add', 'tracked.txt']); git(['commit', '-qm', 'Proof baseline']); git(['clone', '--bare', work, bare]);
 execFileSync('git', ['--git-dir=' + bare, 'update-server-info'], { stdio: 'pipe' });
}
let sequence = 0;
const records = [];
const issued = new Set();
http.createServer(async (req, res) => {
 try {
  const url = new URL(req.url, 'http://example.com');
  if (url.pathname === '/proof/requests') { res.setHeader('content-type', 'application/json'); return res.end(JSON.stringify(records)); }
  if (/^\/(first|second)\.git\//.test(url.pathname)) {
   const candidate = path.resolve(root, '.' + decodeURIComponent(url.pathname));
   if (!candidate.startsWith(root + path.sep)) { res.statusCode = 400; return res.end(); }
   try { res.setHeader('content-type','text/plain'); return res.end(fs.readFileSync(candidate)); }
   catch { res.statusCode = 404; return res.end(); }
  }
  if (url.pathname.endsWith('/models')) {
   const ids = ['gpt-6.1-sol', 'gpt-6-sol', 'claude-opus-4-8'];
   res.setHeader('content-type','application/json');
   return res.end(JSON.stringify({ object:'list', data:ids.map(id=>({id,object:'model',type:'model',display_name:id,created_at:'2026-01-01T00:00:00Z'})), has_more:false }));
  }
  const chunks=[];let size=0;
  for await (const chunk of req) {size+=chunk.length;if(size>4*1024*1024){res.statusCode=413;return res.end();}chunks.push(chunk);}
  let raw=Buffer.concat(chunks); if(req.headers['content-encoding']==='gzip')raw=gunzipSync(raw);
  const body=raw.length?JSON.parse(raw.toString()):{};
  const messages=JSON.stringify(body.messages??body.input??[]);
  const marker=[...messages.matchAll(/SWITCH-PROOF-\d+/g)].at(-1)?.[0]??'helper';
  const answer='Answer '+marker;
  const isMessages=url.pathname.includes('/messages');
  const supplied=req.headers['x-api-key']??String(req.headers.authorization??'').replace(/^Bearer /,'');
  const accepted = isMessages ? ['synthetic-claude-proof','synthetic-claude-subscription'] : ['synthetic-codex-proof','synthetic-codex-subscription'];
  if((isMessages || url.pathname.endsWith('/responses')) && !accepted.includes(supplied)){res.statusCode=401;return res.end('{}');}
  records.push({sequence:++sequence,route:url.pathname,marker,containsQueued:messages.includes('QUEUED-AFTER-HANDOVER'),deniedTool:body.messages?.some(message=>Array.isArray(message.content)&&message.content.some(item=>item.type==='tool_result'&&item.tool_use_id==='toolu-proof-deny'&&item.is_error===true))??false,characters:messages.length});
  if(isMessages){
   if(url.pathname.includes('count_tokens')){res.setHeader('content-type','application/json');return res.end('{"input_tokens":10}');}
   const request=[...messages.matchAll(/Replacement request: ([0-9a-f-]{36})/g)].at(-1)?.[1];
   const askTool=marker==='SWITCH-PROOF-2'&&request&&body.tools?.some(tool=>tool.name==='Bash')&&!issued.has(request);
   if(askTool)issued.add(request);
   const input={command:'printf unapproved > /workspace/provider-proof-not-authorized',description:'Verify preserved approval mode'};
   const message={id:crypto.randomUUID(),type:'message',role:'assistant',model:body.model,content:askTool?[{type:'tool_use',id:'toolu-proof-deny',name:'Bash',input}]:[{type:'text',text:answer}],stop_reason:askTool?'tool_use':'end_turn',usage:{input_tokens:10,output_tokens:5}};
   if(!body.stream){res.setHeader('content-type','application/json');return res.end(JSON.stringify(message));}
   res.setHeader('content-type','text/event-stream');
   const event=(type,value)=>res.write(`event: ${type}\ndata: ${JSON.stringify({type,...value})}\n\n`);
   event('message_start',{message:{...message,content:[],stop_reason:null}});
   event('content_block_start',{index:0,content_block:askTool?{type:'tool_use',id:'toolu-proof-deny',name:'Bash',input:{}}:{type:'text',text:''}});
   event('content_block_delta',{index:0,delta:askTool?{type:'input_json_delta',partial_json:JSON.stringify(input)}:{type:'text_delta',text:answer}});event('content_block_stop',{index:0});
   event('message_delta',{delta:{stop_reason:message.stop_reason,stop_sequence:null},usage:{output_tokens:5}});event('message_stop',{});return res.end();
  }
  if(url.pathname.endsWith('/responses')){
   res.setHeader('content-type','text/event-stream');
   for(const event of [{type:'response.created',response:{id:crypto.randomUUID()}},{type:'response.output_item.done',item:{type:'message',id:crypto.randomUUID(),role:'assistant',content:[{type:'output_text',text:answer}]}},{type:'response.completed',response:{id:crypto.randomUUID(),usage:{input_tokens:10,output_tokens:5,total_tokens:15}}}])res.write(`event: ${event.type}\ndata: ${JSON.stringify(event)}\n\n`);
   return res.end();
  }
  res.setHeader('content-type','application/json');res.end('{}');
 }catch{res.statusCode=500;res.end('{}');console.error('Recording model fixture rejected a request.');}
}).listen(Number(process.env.PROOF_MODEL_PORT || 8080),'0.0.0.0');
