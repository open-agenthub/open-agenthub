// Drive real native exits through the production PTY server and record status callbacks.
// Model replies and credentials are synthetic; native transcript rows are never inserted.
import assert from 'node:assert/strict';
import fs from 'node:fs';
import path from 'node:path';
import http from 'node:http';
import crypto from 'node:crypto';
import { spawn } from 'node:child_process';
import { createRequire } from 'node:module';
const repo = process.env.PROOF_REPO || path.resolve(import.meta.dirname, '../..');
const out = process.env.PROOF_OUT;
const bins = process.env.PROOF_CLI_BIN;
assert(out && bins && path.isAbsolute(out));
fs.mkdirSync(out, { recursive: true });
const require = createRequire('/opt/session-agent/package.json');
const WebSocket = require('ws');
const { Terminal } = require('@xterm/headless');
const requests = [];
const statuses = [];
const listen = server => new Promise(resolve => server.listen(0, '127.0.0.1', resolve));
const sleep = ms => new Promise(resolve => setTimeout(resolve, ms));
async function until(fn) { for (let n = 0; n < 600; n++) { if (child && child.exitCode !== null) throw new Error('session server exited: ' + child.exitCode); try { const value = await fn(); if (value) return value; } catch {} await sleep(200); } throw new Error('capture timeout'); }
const model = http.createServer(async (req, res) => {
  let raw = ''; for await (const chunk of req) raw += chunk;
  const body = raw ? JSON.parse(raw) : {};
  const text = JSON.stringify(body.messages ?? body.input ?? []);
  requests.push({ route: req.url, text });
  const marker = [...text.matchAll(/HISTORY-(?:claude|codex)-\d+/g)].at(-1)?.[0] ?? 'helper';
  const answer = 'Answer ' + marker;
  if (req.url.includes('/messages')) {
    if (req.url.includes('count_tokens')) { res.setHeader('content-type','application/json'); return res.end('{"input_tokens":10}'); }
    const message = { id: crypto.randomUUID(), type: 'message', role: 'assistant', model: body.model,
      content: [{ type: 'text', text: answer }], stop_reason: 'end_turn', usage: { input_tokens: 10, output_tokens: 5 } };
    if (!body.stream) { res.setHeader('content-type','application/json'); return res.end(JSON.stringify(message)); }
    res.setHeader('content-type', 'text/event-stream');
    const event = (type, value) => res.write(`event: ${type}\ndata: ${JSON.stringify({ type, ...value })}\n\n`);
    event('message_start', { message: { ...message, content: [], stop_reason: null } });
    event('content_block_start', { index: 0, content_block: { type: 'text', text: '' } });
    event('content_block_delta', { index: 0, delta: { type: 'text_delta', text: answer } });
    event('content_block_stop', { index: 0 });
    event('message_delta', { delta: { stop_reason: 'end_turn', stop_sequence: null }, usage: { output_tokens: 5 } });
    event('message_stop', {}); return res.end();
  }
  if (req.url === '/v1/responses') {
    res.setHeader('content-type', 'text/event-stream');
    for (const event of [
      { type: 'response.created', response: { id: crypto.randomUUID() } },
      { type: 'response.output_item.done', item: { type: 'message', id: crypto.randomUUID(), role: 'assistant', content: [{ type: 'output_text', text: answer }] } },
      { type: 'response.completed', response: { id: crypto.randomUUID(), usage: { input_tokens: 10, output_tokens: 5, total_tokens: 15 } } }
    ]) res.write(`event: ${event.type}\ndata: ${JSON.stringify(event)}\n\n`);
    return res.end();
  }
  res.setHeader('content-type', 'application/json'); res.end('{}');
});

const hub = http.createServer(async (req,res)=>{
 let raw='';for await(const chunk of req)raw+=chunk;
 if(req.url.endsWith('/status'))statuses.push(JSON.parse(raw).status);
 res.setHeader('content-type','application/json');res.end('{}');
});
await listen(model);await listen(hub);
const modelUrl=`http://127.0.0.1:${model.address().port}`;
let child;
try{
 const agents=(process.env.PROOF_AGENTS||'claude,codex').split(',');
 assert(agents.length&&agents.every(agent=>['claude','codex'].includes(agent)));
 for(const agent of agents)for(const outcome of ['signal','normal']){
  const root=path.join(out,agent+'-'+outcome),home=path.join(root,'home'),work=path.join(root,'work');
  for(const dir of [work,path.join(home,'.claude'),path.join(home,'.codex'),path.join(root,'tmp')])fs.mkdirSync(dir,{recursive:true});
  const apiKey='synthetic-native-exit-proof';
  fs.writeFileSync(path.join(home,'.claude.json'),JSON.stringify({hasCompletedOnboarding:true,autoUpdates:false,
   customApiKeyResponses:{approved:[apiKey.slice(-20)],rejected:[]},projects:{[work]:{hasTrustDialogAccepted:true}}}));
  fs.writeFileSync(path.join(home,'.codex/config.toml'),`openai_base_url = "${modelUrl}/v1"\ncli_auth_credentials_store = "file"\n[projects."${work}"]\ntrust_level = "trusted"\n`);
  fs.writeFileSync(path.join(home,'.codex/auth.json'),JSON.stringify({OPENAI_API_KEY:apiKey}));
  const env={PATH:bins+':'+process.env.PATH,HOME:home,TMPDIR:path.join(root,'tmp'),TERM:'xterm-256color',LANG:'C.UTF-8',
   NODE_PATH:'/opt/session-agent/node_modules',AGENTHUB_DRIVER:path.join(repo,`agent-runtime/${agent}/driver.js`),
   AGENTHUB_SESSION_ID:'exit-proof',AGENTHUB_MODE:'interactive',AGENTHUB_WORKDIR:work,AGENTHUB_PORT:'18674',
   AGENTHUB_CALLBACK_URL:`http://127.0.0.1:${hub.address().port}/internal/sessions/exit-proof`,AGENTHUB_CALLBACK_TOKEN:'synthetic-exit-token',
   AGENTHUB_SKILLS_SYNC_SECONDS:'0',AGENTHUB_MODEL_DISCOVERY:'0',AGENTHUB_AUTH_WATCHER_PIDFILE:path.join(root,'absent.pid'),
   TERMINATION_MESSAGE_PATH:path.join(root,'termination.txt'),AGENTHUB_PROMPT:`HISTORY-${agent}-0`};
  if(agent==='claude')Object.assign(env,{ANTHROPIC_API_KEY:apiKey,ANTHROPIC_BASE_URL:modelUrl,CLAUDE_CODE_DISABLE_NONESSENTIAL_TRAFFIC:'1',DISABLE_AUTOUPDATER:'1',AGENTHUB_CLAUDE_SESSION_ID:crypto.randomUUID()});
  else env.CODEX_HOME=path.join(home,'.codex');
  const log=fs.openSync(path.join(root,'server.log'),'a');
  child=spawn(process.execPath,[path.join(repo,'agent-runtime/common/server.js')],{env,cwd:work,detached:true,stdio:['ignore',log,log]});
  const exited=new Promise(resolve=>child.once('exit',resolve));
  const transcript=async()=>{const response=await fetch('http://127.0.0.1:18674/agenthub/transcript?from=0',{headers:{'X-Agent-Token':env.AGENTHUB_CALLBACK_TOKEN}});assert(response.ok);return response.text();};
  await until(async()=>{await transcript();return true;});
  const term=new Terminal({cols:120,rows:36,allowProposedApi:true}),socket=new WebSocket('ws://127.0.0.1:18674/');
  socket.on('message',data=>term.write(data.toString()));
  term.onData(data=>{if(socket.readyState===WebSocket.OPEN)socket.send(JSON.stringify({type:'input',data}));});
  await new Promise((resolve,reject)=>{socket.once('open',resolve);socket.once('error',reject);});
  socket.send(JSON.stringify({type:'resize',cols:120,rows:36}));
  try{
   await until(async()=>(await transcript()).includes(`Answer HISTORY-${agent}-0`));
   const inference=()=>requests.filter(item=>item.text.includes(`HISTORY-${agent}-0`)&&!item.route.includes('count_tokens'));
   const before=inference().length,start=statuses.length;
   if(outcome==='signal'){
    const native=fs.readFileSync(`/proc/${child.pid}/task/${child.pid}/children`,'utf8').trim().split(/\s+/).map(Number);
    assert.equal(native.length,1,'fault only the isolated real PTY child');process.kill(native[0],'SIGKILL');
   }else{
    await until(()=>Array.from({length:term.rows},(_,index)=>term.buffer.active.getLine(term.buffer.active.viewportY+index)?.translateToString(true)??'').some(line=>/^\s*[›❯]/.test(line)&&!line.includes('HISTORY-')));
    socket.send(JSON.stringify({type:'input',data:'/exit'}));await sleep(200);socket.send(JSON.stringify({type:'input',data:'\r'}));
   }
   let timer;try{await Promise.race([exited,new Promise((_,reject)=>{timer=setTimeout(()=>reject(new Error('native exit timeout')),30000);})]);}finally{clearTimeout(timer);}
   const expected=outcome==='signal'?'Failed':'Succeeded';
   assert.equal(statuses.slice(start).at(-1),expected,'real callback must classify native exit');
   if(outcome==='signal'){assert.notEqual(child.exitCode,0);assert.match(fs.readFileSync(env.TERMINATION_MESSAGE_PATH,'utf8'),/signal 9/);}
   else assert.equal(child.exitCode,0);
   assert.equal(inference().length,before,'exit must not replay the initial task');
   console.log(`${agent} ${outcome}: callback=${expected}, runtime exit=${child.exitCode}, no replay`);
  }finally{
   socket.close();term.dispose();if(child.exitCode===null){process.kill(-child.pid,'SIGTERM');await exited;}child=null;fs.closeSync(log);
  }
 }
}finally{if(child){try{process.kill(-child.pid,'SIGTERM');}catch{}}model.close();hub.close();}
