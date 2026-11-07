// Capture genuine native histories through the production PTY server. Only the model
// and hub callbacks are local stand-ins; no native transcript rows are inserted.
import assert from 'node:assert/strict';
import fs from 'node:fs';
import path from 'node:path';
import http from 'node:http';
import crypto from 'node:crypto';
import { spawn, execFileSync } from 'node:child_process';
import { createRequire } from 'node:module';
const repo = path.resolve(import.meta.dirname, '../..');
const out = process.env.PROOF_OUT;
const bins = process.env.PROOF_CLI_BIN;
assert(out && bins && path.isAbsolute(out));
fs.mkdirSync(out, { recursive: true });
const require = createRequire('/opt/session-agent/package.json');
const WebSocket = require('ws');
const { Terminal } = require('@xterm/headless');
const requests = [];
const archives = [];
let queuedAfterCancel = [];
let releaseBusyModel, busyHeld = false;
const listen = server => new Promise(resolve => server.listen(0, '127.0.0.1', resolve));
const sleep = ms => new Promise(resolve => setTimeout(resolve, ms));
async function until(fn) { for (let n = 0; n < 600; n++) { if (child && child.exitCode !== null) throw new Error('session server exited: ' + child.exitCode); try { const value = await fn(); if (value) return value; } catch {} await sleep(200); } throw new Error('capture timeout'); }
const model = http.createServer(async (req, res) => {
  let raw = ''; for await (const chunk of req) raw += chunk;
  const body = raw ? JSON.parse(raw) : {};
  const text = JSON.stringify(body.messages ?? body.input ?? []);
  requests.push({ route: req.url, text });
  if(process.env.PROOF_CLAUDE_BUSY==='1'&&!busyHeld&&text.includes('CANCEL-QUEUED-native-claude')){
    busyHeld=true;await new Promise(resolve=>{releaseBusyModel=resolve;});
  }
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
const hub = http.createServer(async (req, res) => {
  const chunks = []; for await (const chunk of req) chunks.push(chunk);
  if (req.url.startsWith('/state/')) archives.push({ route: req.url, bytes: Buffer.concat(chunks) });
  res.setHeader('content-type', 'application/json');
  if(req.url.includes('/messages')){const messages=queuedAfterCancel;queuedAfterCancel=[];return res.end(JSON.stringify({messages}));}
  res.end(req.url.endsWith('/agent-policy') ? '{"decision":"allow"}' : req.url.includes('/messages') ? '{"messages":[]}' : '{}');
});
await listen(model); await listen(hub);
const modelUrl = `http://127.0.0.1:${model.address().port}`;
let child;
try {
  const agents=(process.env.PROOF_AGENTS||'claude,codex').split(',');
  assert(agents.length&&agents.every(agent=>['claude','codex'].includes(agent)));
  for (const agent of agents) {
    const root = path.join(out, agent); const home = path.join(root, 'home'); const work = path.join(root, 'work');
    for (const dir of [work, path.join(home, '.claude'), path.join(home, '.codex'), path.join(root, 'tmp')]) fs.mkdirSync(dir, { recursive: true });
    const apiKey = 'synthetic-history-key';
    fs.writeFileSync(path.join(home, '.claude.json'), JSON.stringify({ hasCompletedOnboarding: true, autoUpdates: false,
      customApiKeyResponses: { approved: [apiKey.slice(-20)], rejected: [] }, projects: { [work]: { hasTrustDialogAccepted: true } } }));
    fs.writeFileSync(path.join(home, '.codex/config.toml'), [
      'model = "gpt-5.4"', 'model_provider = "proof"', 'cli_auth_credentials_store = "file"',
      '[model_providers.proof]', 'name = "Proof"', `base_url = "${modelUrl}/v1"`,
      'wire_api = "responses"', 'env_key = "CODEX_API_KEY"', 'supports_websockets = false',
      `[projects."${work}"]`, 'trust_level = "trusted"', ''
    ].join('\n'));
    // Each provider gets an isolated home and only its own synthetic credential.
    const env = { PATH: bins + ':' + process.env.PATH, HOME: home, TMPDIR: path.join(root, 'tmp'), TERM: 'xterm-256color', LANG: 'C.UTF-8',
      NODE_PATH: '/opt/session-agent/node_modules', AGENTHUB_DRIVER: path.join(repo, `agent-runtime/${agent}/driver.js`),
      AGENTHUB_SESSION_ID: 'history', AGENTHUB_MODE: 'interactive', AGENTHUB_WORKDIR: work, AGENTHUB_PORT: '18674',
      AGENTHUB_CALLBACK_URL: `http://127.0.0.1:${hub.address().port}/internal/sessions/history`,
      AGENTHUB_CALLBACK_TOKEN: 'synthetic-history-callback', AGENTHUB_SKILLS_SYNC_SECONDS: '0',
      AGENTHUB_AUTH_WATCHER_PIDFILE: path.join(root, 'tmp', 'absent-watcher.pid'),
      AGENTHUB_MODEL_DISCOVERY: '0', AGENTHUB_PROVIDER_SWITCH_ID: '11111111-1111-4111-8111-111111111111',
      AGENTHUB_STATE_PUT_URL: `http://127.0.0.1:${hub.address().port}/state/${agent}`, AGENTHUB_PROMPT: `HISTORY-${agent}-0` };
    if (agent === 'claude') Object.assign(env, { ANTHROPIC_API_KEY: apiKey, ANTHROPIC_BASE_URL: modelUrl,
      CLAUDE_CODE_DISABLE_NONESSENTIAL_TRAFFIC: '1', DISABLE_AUTOUPDATER: '1', AGENTHUB_CLAUDE_SESSION_ID: crypto.randomUUID() });
    else Object.assign(env, { CODEX_HOME: path.join(home, '.codex'), CODEX_API_KEY: apiKey });
    if (agent === 'codex' && process.env.PROOF_NATIVE_DEFAULT === '1') {
      fs.writeFileSync(path.join(home, '.codex/config.toml'), [
        `openai_base_url = "${modelUrl}/v1"`, 'cli_auth_credentials_store = "file"', `[projects."${work}"]`, 'trust_level = "trusted"', ''
      ].join('\n'));
      fs.writeFileSync(path.join(home, '.codex/auth.json'), JSON.stringify({ OPENAI_API_KEY: apiKey }));
      delete env.CODEX_API_KEY;

    }
    if(agent==='claude'&&process.env.PROOF_HTTPS_PROXY){
      Object.assign(env,{HTTPS_PROXY:process.env.PROOF_HTTPS_PROXY,NODE_EXTRA_CA_CERTS:process.env.PROOF_EXTRA_CA,NO_PROXY:'localhost,127.0.0.1'});
    }
    if(agent==='claude'&&process.env.PROOF_SUBSCRIPTION==='1'){
      delete env.ANTHROPIC_API_KEY;
      fs.writeFileSync(path.join(home,'.claude/.credentials.json'),JSON.stringify({claudeAiOauth:{accessToken:'synthetic-claude-subscription',refreshToken:'synthetic-refresh',expiresAt:Date.now()+86400000,scopes:['user:inference','user:profile','user:sessions:claude_code'],subscriptionType:'pro',rateLimitTier:'default_claude_pro'}}));
      const config=JSON.parse(fs.readFileSync(path.join(home,'.claude.json'),'utf8'));
      config.oauthAccount={accountUuid:'11111111-1111-4111-8111-111111111111',emailAddress:'proof@example.com',organizationUuid:'22222222-2222-4222-8222-222222222222',organizationName:'Proof workspace'};
      fs.writeFileSync(path.join(home,'.claude.json'),JSON.stringify(config));
    }
    if(agent==='claude'&&process.env.PROOF_CLAUDE_MOD==='1'){
      const mod=path.join(root,'fleet-mod');
      fs.cpSync(path.join(repo,'agent-runtime/claude/mods/agenthub-fleet'),mod,{recursive:true,filter:file=>!file.includes('node_modules')});
      const register=path.join(mod,'hooks/register.js');
      fs.writeFileSync(register,fs.readFileSync(register,'utf8').replaceAll('127.0.0.1:7681','127.0.0.1:18674'));
      env.CLAUDE_CODE_PLUGIN_DIRS=mod;env.AGENTHUB_MOD_TOKEN='synthetic-native-mod';
      delete env.CLAUDE_CODE_DISABLE_NONESSENTIAL_TRAFFIC;
    }
    if (process.env.PROOF_SOURCE_PREPARE === '1' && agent === 'claude') {
      const settings = execFileSync('bash', [path.join(repo, 'agent-runtime/claude/hooks/mcp-policy-hook.sh'), '--settings'],
        { env: { ...env, RUNTIME: path.join(repo, 'agent-runtime'), AGENTHUB_RUNTIME: path.join(repo, 'agent-runtime/claude/hooks'), AGENTHUB_NODE_BIN: process.execPath } });
      fs.writeFileSync(path.join(home, '.claude/settings.json'), settings);
    }
    if(process.env.PROOF_MISSING_NATIVE_STATE==='1'){
      Object.assign(env,{AGENTHUB_REQUIRE_NATIVE_RESUME:'1',AGENTHUB_RESUME:'1',AGENTHUB_STATE_RESTORED:'0'});
      delete env.AGENTHUB_PROVIDER_SWITCH_ID;
    }
    let command = process.execPath, args = [path.join(repo, 'agent-runtime/session-agent/server.js')];
    if (process.env.PROOF_SOURCE_PREPARE === '1' && agent === 'codex') {
      assert(process.env.PROOF_PROOT && path.isAbsolute(process.env.PROOF_PROOT), 'managed source proof requires PROOF_PROOT');
      const runtime = path.join(root, 'runtime'), managed = path.join(root, 'managed');
      fs.mkdirSync(runtime, { recursive: true }); fs.mkdirSync(managed, { recursive: true });
      for (const name of ['common', 'codex', 'files', 'browser']) fs.cpSync(path.join(repo, 'agent-runtime', name), path.join(runtime, name), { recursive: true });
      for (const name of ['server.js', 'package.json']) fs.copyFileSync(path.join(repo, 'agent-runtime/session-agent', name), path.join(runtime, name));
      fs.cpSync('/opt/session-agent/node_modules', path.join(runtime, 'node_modules'), { recursive: true });
      fs.copyFileSync(path.join(repo, 'agent-runtime/codex/requirements.toml'), path.join(managed, 'requirements.toml'));
      const inside = '/proof/codex';
      const config = path.join(home, '.codex/config.toml');
      fs.writeFileSync(config, fs.readFileSync(config, 'utf8').split(work).join(inside + '/work'));
      fs.mkdirSync(path.join(root, 'tmp', 'agenthub-private'), { recursive: true });
      Object.assign(env, { HOME: inside + '/home', CODEX_HOME: inside + '/home/.codex', TMPDIR: '/tmp/agenthub-private',
        AGENTHUB_WORKDIR: inside + '/work', AGENTHUB_DRIVER: '/opt/session-agent/codex/driver.js',
        AGENTHUB_AUTH_WATCHER_PIDFILE: '/tmp/absent-watcher.pid', PATH: '/opt/proof-clis/node_modules/.bin:' + process.env.PATH });
      command = process.env.PROOF_PROOT;
      args = ['-b', runtime + ':/opt/session-agent', '-b', managed + ':/etc/codex', '-b', out + ':/proof',
        '-b', path.resolve(bins, '../..') + ':/opt/proof-clis', '-b', path.join(root, 'tmp') + ':/tmp',
        '-w', inside + '/work', process.execPath, '/opt/session-agent/server.js'];
    }
    const log = fs.openSync(path.join(root, 'server.log'), 'a');
    child = spawn(command, args, { env, cwd: work, detached: true, stdio: ['ignore', log, log] });
    if(process.env.PROOF_MISSING_NATIVE_STATE==='1'){
      const terminal=new Terminal({cols:120,rows:36,allowProposedApi:true});
      let socket;
      try{
        for(let attempt=0;attempt<100&&child.exitCode===null;attempt++){
          if(!socket||socket.readyState===WebSocket.CLOSED){
            socket=new WebSocket('ws://127.0.0.1:18674/');
            socket.on('error',()=>{});
            socket.on('message',data=>terminal.write(data.toString()));
            socket.once('open',()=>socket.send(JSON.stringify({type:'resize',cols:120,rows:36})));
            terminal.onData(data=>{if(socket.readyState===WebSocket.OPEN)socket.send(JSON.stringify({type:'input',data}));});
          }
          await sleep(100);
        }
        const replayed=requests.filter(request=>request.text.includes('HISTORY-'+agent+'-0')).length;
        console.log(agent+': missing native archive, process exit='+child.exitCode+', stale-prompt model requests='+replayed);
        assert.equal(replayed,0,'A missing native archive must never replay the original source prompt');
        assert.equal(child.exitCode,1,'A required native resume must fail closed before spawning a fresh conversation');
        assert.match(fs.readFileSync(path.join(root,'server.log'),'utf8'),/Native resume is required; saved conversation state was not restored/);
      }finally{socket?.close();terminal.dispose();}
      child=null;
      continue;
    }
    const transcript = async () => {
      const response = await fetch('http://127.0.0.1:18674/agenthub/transcript?from=0', { headers: { 'X-Agent-Token': env.AGENTHUB_CALLBACK_TOKEN } });
      assert(response.ok); return response.text();
    };
    await until(async () => { await transcript(); return true; });
    const term = new Terminal({ cols: 120, rows: 36, allowProposedApi: true });
    const socket = new WebSocket('ws://127.0.0.1:18674/');
    socket.on('message', data => term.write(data.toString()));
    term.onData(data => { if (socket.readyState === WebSocket.OPEN) socket.send(JSON.stringify({ type: 'input', data })); });
    await new Promise((resolve, reject) => { socket.once('open', resolve); socket.once('error', reject); });
    socket.send(JSON.stringify({ type: 'resize', cols: 120, rows: 36 }));
    try {
      await until(async () => (await transcript()).includes(`Answer HISTORY-${agent}-0`));
      const authority = { 'X-Agent-Token': env.AGENTHUB_CALLBACK_TOKEN, 'Content-Type': 'application/json' };
      const route = 'http://127.0.0.1:18674/agenthub/provider-switch';
      const state = await until(async () => {
        const current = await (await fetch(route, { headers: authority })).json();
        return current.ready && current.markerSeen && current.nativeId && current;
      });
      assert(state.draining);
      socket.send(JSON.stringify({ type: 'input', data: 'BLOCKED-BEFORE-RELEASE\r' }));
      await sleep(500);
      assert(!(await transcript()).includes('BLOCKED-BEFORE-RELEASE'));
      const release = await fetch(route, { method: 'POST', headers: authority,
        body: JSON.stringify({ requestId: env.AGENTHUB_PROVIDER_SWITCH_ID, action: 'release', nativeId: state.nativeId }) });
      assert.equal(release.status, 200, await release.clone().text());
      assert.equal((await release.json()).draining, false);
      assert(archives.some(item => item.route === '/state/' + agent && item.bytes.length > 0));
      fs.writeFileSync(path.join(root, 'state.tgz'), archives.filter(item => item.route === '/state/' + agent).at(-1).bytes);
      if (process.env.PROOF_SOURCE_PREPARE === '1') {
        const nextRequest = '22222222-2222-4222-8222-222222222222';
        if(process.env.PROOF_SOURCE_SHELL==='1'){
          const shell=new WebSocket('ws://127.0.0.1:18674/shell');
          await new Promise((resolve,reject)=>{shell.once('message',resolve);shell.once('error',reject);});
          const refused=await fetch(route,{method:'POST',headers:authority,body:JSON.stringify({requestId:nextRequest,action:'prepare'})});
          assert.equal(refused.status,409);
          await new Promise(resolve=>{shell.once('close',resolve);shell.close();});
          console.log(agent+': actual loopback bash shell refuses source preparation and closes cleanly');
        }
        const prepared = await until(async () => {
          const response = await fetch(route, { method: 'POST', headers: authority, body: JSON.stringify({ requestId: nextRequest, action: 'prepare' }) });
          return response.ok && await response.json();
        });
        assert(prepared.checkpointed && prepared.draining);
        if(agent==='codex')queuedAfterCancel=[{id:'cancel-queued',body:'CANCEL-QUEUED-native-codex',priority:true,fromTitle:'Proof'}];
        const cancelled = await fetch(route, { method: 'DELETE', headers: authority, body: JSON.stringify({ requestId: nextRequest }) });
        assert.equal(cancelled.status, 200);
        if(agent==='codex')await until(async()=> (await transcript()).includes('CANCEL-QUEUED-native-codex'));
    if(agent==='claude'&&process.env.PROOF_CLAUDE_MOD==='1'){
          const pushed=await fetch('http://127.0.0.1:18674/agenthub/messages',{method:'POST',headers:authority,body:JSON.stringify({id:'cancel-mod',body:'CANCEL-QUEUED-native-claude',priority:true,fromTitle:'Proof'})});
          assert.equal(pushed.status,200);
          assert.equal((await pushed.json()).delivered,'queued-for-mod');
          await until(async()=> (await transcript()).includes('CANCEL-QUEUED-native-claude'));
          if(process.env.PROOF_CLAUDE_BUSY==='1'){
            await until(()=>releaseBusyModel);
            const busy=await fetch('http://127.0.0.1:18674/agenthub/messages',{method:'POST',headers:authority,body:JSON.stringify({id:'busy-mod',body:'BUSY-QUEUED-native-claude',priority:true,fromTitle:'Proof'})});
            assert.equal((await busy.json()).delivered,'queued-for-mod');
            releaseBusyModel();releaseBusyModel=null;
            await until(()=>requests.some(item=>item.text.includes('BUSY-QUEUED-native-claude')));
            const rows=(await transcript()).split('\n').filter(line=>line.includes('BUSY-QUEUED-native-claude')).map(line=>{const row=JSON.parse(line);return {type:row.type,isMeta:row.isMeta??false};});
            fs.writeFileSync(path.join(root,'busy-delivery.json'),JSON.stringify({modelReceived:true,rows}));
            console.log('claude: real busy-turn mod receipt reached model; persisted native row types '+JSON.stringify(rows));
          }
        }
        const stopRequest = '33333333-3333-4333-8333-333333333333';
        await until(async () => (await fetch(route, { method: 'POST', headers: authority, body: JSON.stringify({ requestId: stopRequest, action: 'prepare' }) })).ok);
        const stopped = await fetch(route, { method: 'POST', headers: authority, body: JSON.stringify({ requestId: stopRequest, action: 'stop' }) });
        assert.equal(stopped.status, 200, await stopped.clone().text());
        assert((await stopped.json()).stopped);
        const lateCancel = await fetch(route, { method: 'DELETE', headers: authority, body: JSON.stringify({ requestId: stopRequest }) });
        assert.equal(lateCancel.status, 409);
        console.log(agent + ': actual native prompt/Stop hooks, source reservation/cancellation, graceful native quit, acknowledged final archive/usage and refused late cancellation passed');
      }
      let text = await transcript();
      if(process.env.PROOF_RESUME_ARCHIVE==='1'){
        socket.close();
        const stopped=new Promise(resolve=>child.once('exit',resolve));
        process.kill(-child.pid,'SIGTERM');await stopped;child=null;
        // Codex's native index stores absolute rollout paths. A replacement pod has the
        // same HOME path on a fresh volume; keep that path while replacing its contents.
        const previousHome=path.join(root,'retired-home');fs.renameSync(home,previousHome);
        const resumedHome=home;fs.mkdirSync(resumedHome,{recursive:true});
        const archive=path.join(root,'resume-state.tgz');
        fs.writeFileSync(archive,archives.filter(item=>item.route==='/state/'+agent).at(-1).bytes);
        execFileSync('tar',['xzf',archive,'-C',resumedHome]);
        const nativeDir=agent==='claude'?'.claude':'.codex';fs.mkdirSync(path.join(resumedHome,nativeDir),{recursive:true});
        for(const name of agent==='claude'?['.claude.json']:['.codex/config.toml','.codex/auth.json']){
          const source=path.join(previousHome,name);if(fs.existsSync(source))fs.copyFileSync(source,path.join(resumedHome,name));
        }
        const resumedEnv={...env,HOME:resumedHome,AGENTHUB_RESUME:'1',AGENTHUB_STATE_RESTORED:'1',
          AGENTHUB_REQUIRE_NATIVE_RESUME:'1',AGENTHUB_CLAUDE_SESSION_ID:state.nativeId,AGENTHUB_NATIVE_SESSION_ID:state.nativeId,
          AGENTHUB_PROMPT:'STALE-SOURCE-PROMPT-MUST-NOT-RUN'};
        if(agent==='codex')resumedEnv.CODEX_HOME=path.join(resumedHome,'.codex');
        delete resumedEnv.AGENTHUB_PROVIDER_SWITCH_ID;
        if(agent==='codex'&&process.env.PROOF_POISON_NATIVE_POINTER==='1'){
          const sessions=path.join(resumedHome,'.codex/sessions');
          const before=new Set(fs.readdirSync(sessions,{recursive:true}).filter(file=>file.endsWith('.jsonl')));
          const other=spawn(path.join(bins,'codex'),['--no-daemon','exec','--sandbox','danger-full-access','--json','--dangerously-bypass-hook-trust','--skip-git-repo-check','OTHER-NATIVE-CONVERSATION'],{env:resumedEnv,cwd:work,stdio:['ignore',log,log]});
          const exited=await new Promise((resolve,reject)=>{other.once('exit',resolve);other.once('error',reject);});
          assert.equal(exited,0,'the second native conversation must be generated by the real CLI');
          const added=fs.readdirSync(sessions,{recursive:true}).filter(file=>file.endsWith('.jsonl')&&!before.has(file));
          assert.equal(added.length,1);
          const metadata=JSON.parse(fs.readFileSync(path.join(sessions,added[0]),'utf8').split('\n')[0]);
          assert.equal(metadata.type,'session_meta');assert.notEqual(metadata.payload.id,state.nativeId);
          fs.writeFileSync(path.join(resumedHome,'.codex/agenthub-thread-id'),metadata.payload.id+'\n');
        }

        child=spawn(process.execPath,[path.join(repo,'agent-runtime/session-agent/server.js')],{env:resumedEnv,cwd:work,detached:true,stdio:['ignore',log,log]});
        await until(async()=>{await transcript();return true;});
        const resumedTerm=new Terminal({cols:120,rows:36,allowProposedApi:true});
        const resumedSocket=new WebSocket('ws://127.0.0.1:18674/');
        resumedSocket.on('message',data=>resumedTerm.write(data.toString()));
        resumedTerm.onData(data=>{if(resumedSocket.readyState===WebSocket.OPEN)resumedSocket.send(JSON.stringify({type:'input',data}));});
        await new Promise((resolve,reject)=>{resumedSocket.once('open',resolve);resumedSocket.once('error',reject);});
        resumedSocket.send(JSON.stringify({type:'resize',cols:120,rows:36}));
        try{
          if(agent==='codex'&&process.env.PROOF_POISON_NATIVE_POINTER==='1'){
            const selected=await until(async()=>{const value=await(await fetch(route,{headers:authority})).json();return value.ready&&value.nativeId&&value;});
            console.log('codex: committed native identity matches selected archive identity='+String(selected.nativeId===state.nativeId));
            assert.equal(selected.nativeId,state.nativeId,'Strict resume must select the committed native identity, not another archived thread');
          }
          await until(async()=>{const current=await(await fetch(route,{headers:authority})).json();return current.ready&&current.nativeId===state.nativeId;});
          await until(()=>Array.from({length:resumedTerm.rows},(_,index)=>resumedTerm.buffer.active.getLine(resumedTerm.buffer.active.viewportY+index)?.translateToString(true)??'').some(line=>/^\s*[›❯]/.test(line)&&!line.includes('HISTORY-')));
          const prior=await transcript();assert(prior.includes('HISTORY-'+agent+'-0'));
          resumedSocket.send(JSON.stringify({type:'input',data:'FOLLOWUP-AFTER-NATIVE-RESUME'}));await sleep(200);
          resumedSocket.send(JSON.stringify({type:'input',data:'\r'}));
          await until(()=>requests.some(item=>item.text.includes('FOLLOWUP-AFTER-NATIVE-RESUME')&&item.text.includes('HISTORY-'+agent+'-0')));
          assert(!requests.some(item=>item.text.includes('STALE-SOURCE-PROMPT-MUST-NOT-RUN')));
          if(agent==='codex'&&process.env.PROOF_POISON_NATIVE_POINTER==='1')assert(!requests.filter(item=>item.text.includes('FOLLOWUP-AFTER-NATIVE-RESUME')).some(item=>item.text.includes('OTHER-NATIVE-CONVERSATION')));
          await until(async()=>(await transcript()).includes('FOLLOWUP-AFTER-NATIVE-RESUME'));
          text=await transcript();
          console.log(agent+': real archive restored into fresh home, same native identity/history, new owner turn and no stale source prompt passed');
        }finally{
          fs.writeFileSync(path.join(root,'resume-terminal.txt'),Array.from({length:resumedTerm.rows},(_,index)=>resumedTerm.buffer.active.getLine(resumedTerm.buffer.active.viewportY+index)?.translateToString(true)??'').join('\n'));
          resumedSocket.close();resumedTerm.dispose();
        }
      }
      fs.writeFileSync(path.join(out, agent + '.jsonl'), text);
      socket.close();
      const reconnect = new WebSocket('ws://127.0.0.1:18674/');
      await new Promise((resolve, reject) => { reconnect.once('open', resolve); reconnect.once('error', reject); });
      if(process.env.PROOF_RESUME_ARCHIVE==='1'){
        const continued=await transcript();assert(continued.includes('HISTORY-'+agent+'-0'));assert(continued.includes('FOLLOWUP-AFTER-NATIVE-RESUME'));
      }else assert.equal(await transcript(), text); reconnect.close();
      console.log(agent + ': native target handover, reserved input, state archive, release and reconnect passed');
    } finally {
      fs.writeFileSync(path.join(root, 'terminal.txt'), Array.from({ length: term.rows }, (_, n) =>
        term.buffer.active.getLine(term.buffer.active.viewportY + n)?.translateToString(true) ?? '').join('\n'));
      socket.close(); term.dispose();
      process.kill(-child.pid, 'SIGTERM'); await new Promise(resolve => child.once('exit', resolve)); child = null;
    }
  }
  fs.writeFileSync(path.join(out, 'model-requests.json'), JSON.stringify(requests, null, 2));
} finally { releaseBusyModel?.(); if (child) { try { process.kill(-child.pid, 'SIGTERM'); } catch {} } model.close(); hub.close(); }
