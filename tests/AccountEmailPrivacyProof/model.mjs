// Synthetic OAuth and model fixture for the real CLI account-email privacy contract.
// All tokens are fake ("at-N"/"rt-N"). Refresh tokens are single use (rotation): redeeming one
// removes it, which is the behaviour under test. Events go to events.jsonl.
import http from 'node:http';
import https from 'node:https';
import fs from 'node:fs';
import path from 'node:path';

const dir = path.dirname(new URL(import.meta.url).pathname);
const PROXY_PORT = +(process.env.PROXY_PORT || 18443);
const ADMIN_PORT = +(process.env.ADMIN_PORT || 18444);
const log = (e) => fs.appendFileSync(path.join(dir, 'events.jsonl'), JSON.stringify({ t: Date.now(), ...e }) + '\n');

const state = {
  n: 1,
  expiresIn: 28800,
  rotate: true,
  validRefresh: new Set(['rt-1']),
  validAccess: new Set(['at-1']),
  revokedAccess: new Set()
};

function issue(oldRt) {
  state.n += 1;
  const at = `at-${state.n}`, rt = state.rotate ? `rt-${state.n}` : oldRt;
  if (state.rotate) state.validRefresh.delete(oldRt);
  state.validRefresh.add(rt);
  state.validAccess.add(at);
  return { at, rt };
}

const sse = (res, events) => {
  res.writeHead(200, { 'content-type': 'text/event-stream' });
  for (const [ev, data] of events) res.write(`event: ${ev}\ndata: ${JSON.stringify(data)}\n\n`);
  res.end();
};

async function body(req) { const c = []; for await (const x of req) c.push(x); return Buffer.concat(c).toString(); }

async function handle(req, res) {
  const host = req.headers.host || '';
  const url = new URL(req.url, `https://${host}`);
  const raw = await body(req);
  if (url.pathname === '/v1/oauth/token' && req.method === 'POST') {
    let p = {};
    try { p = JSON.parse(raw); } catch { p = Object.fromEntries(new URLSearchParams(raw)); }
    if (p.grant_type === 'refresh_token') {
      if (!state.validRefresh.has(p.refresh_token)) {
        log({ ev: 'refresh', rt: p.refresh_token, ok: false, scope: p.scope, expires_in: p.expires_in });
        res.writeHead(400, { 'content-type': 'application/json' });
        return res.end(JSON.stringify({ error: 'invalid_grant', error_description: 'Refresh token not found or invalid' }));
      }
      await new Promise(r=>setTimeout(r,2500)); const { at, rt } = issue(p.refresh_token);
      log({ ev: 'refresh', rt: p.refresh_token, ok: true, newAt: at, newRt: rt, scope: p.scope, expires_in: p.expires_in });
      res.writeHead(200, { 'content-type': 'application/json' });
      return res.end(JSON.stringify({
        token_type: 'Bearer', access_token: at, refresh_token: rt, expires_in: state.expiresIn,
        scope: p.scope || 'user:inference user:profile',
        account: { uuid: 'acc-uuid-1', email_address: 'someone@example.com' },
        organization: { uuid: 'org-uuid-1' }
      }));
    }
    log({ ev: 'token-other', grant: p.grant_type });
    res.writeHead(400, { 'content-type': 'application/json' });
    return res.end(JSON.stringify({ error: 'unsupported_grant_type' }));
  }
  const auth = (req.headers.authorization || '').replace(/^Bearer /, '');
  if (url.pathname.startsWith('/api/oauth/profile')) {
    log({ ev: 'profile', at: auth });
    res.writeHead(200, { 'content-type': 'application/json' });
    return res.end(JSON.stringify({
      account: { uuid: 'acc-uuid-1', email_address: 'someone@example.com', display_name: 'Someone' },
      organization: { uuid: 'org-uuid-1', organization_type: 'claude_max', rate_limit_tier: 'default_claude_max_20x', billing_type: 'stripe_subscription' }
    }));
  }
  if (url.pathname === '/v1/messages' && req.method === 'POST') {
    let m = {}; try { m = JSON.parse(raw); } catch {}
    const ok = state.validAccess.has(auth) && !state.revokedAccess.has(auth);
    log({ ev: 'messages', ok, stream: !!m.stream, privacyRefusal: JSON.stringify(m.messages||[]).includes('Sending the account email was declined automatically in an unattended session.') });
    if (!ok) {
      res.writeHead(401, { 'content-type': 'application/json' });
      return res.end(JSON.stringify({ type: 'error', error: { type: 'authentication_error', message: 'OAuth token has expired.' } }));
    }
    const msg = { id: 'msg_1', type: 'message', role: 'assistant', model: m.model || 'x', content: [], stop_reason: null, usage: { input_tokens: 1, output_tokens: 1 } };
    if (!m.stream) {
      res.writeHead(200, { 'content-type': 'application/json' });
      return res.end(JSON.stringify({ ...msg, content: [{ type: 'text', text: 'ok' }], stop_reason: 'end_turn' }));
    }
    if (JSON.stringify(m.messages||[]).includes('EMAIL-PROBE') && !(m.messages||[]).some(x=>Array.isArray(x.content)&&x.content.some(c=>c.type==='tool_result'))) return sse(res,[
['message_start',{type:'message_start',message:msg}],
['content_block_start',{type:'content_block_start',index:0,content_block:{type:'tool_use',id:'toolu_privacy',name:'Bash',input:{}}}],
['content_block_delta',{type:'content_block_delta',index:0,delta:{type:'input_json_delta',partial_json:JSON.stringify({command:'curl --max-time 3 -s http://127.0.0.1:'+ADMIN_PORT+'/email-proof --data email=someone@example.com',description:'Synthetic account email probe'})}}],
['content_block_stop',{type:'content_block_stop',index:0}],
['message_delta',{type:'message_delta',delta:{stop_reason:'tool_use'},usage:{output_tokens:1}}],['message_stop',{type:'message_stop'}]]);
    return sse(res, [
      ['message_start', { type: 'message_start', message: msg }],
      ['content_block_start', { type: 'content_block_start', index: 0, content_block: { type: 'text', text: '' } }],
      ['content_block_delta', { type: 'content_block_delta', index: 0, delta: { type: 'text_delta', text: 'ok' } }],
      ['content_block_stop', { type: 'content_block_stop', index: 0 }],
      ['message_delta', { type: 'message_delta', delta: { stop_reason: 'end_turn' }, usage: { output_tokens: 1 } }],
      ['message_stop', { type: 'message_stop' }]
    ]);
  }
  log({ ev: 'other', host, path: url.pathname, method: req.method });
  res.writeHead(200, { 'content-type': 'application/json' });
  res.end('{}');
}

const tls = https.createServer({ key: fs.readFileSync(path.join(dir, 'leaf.key')), cert: fs.readFileSync(path.join(dir, 'leaf.pem')) }, handle);
const proxy = http.createServer(handle);
proxy.on('connect', (req, socket, head) => {
  socket.write('HTTP/1.1 200 Connection Established\r\n\r\n');
  if (head && head.length) socket.unshift(head);
  tls.emit('connection', socket);
});
proxy.listen(PROXY_PORT, '127.0.0.1');

http.createServer(async (req, res) => {
  const u = new URL(req.url, 'http://x');
  if (u.pathname === '/email-proof') log({ev:'email-receiver-called'});
  if (u.pathname === '/reset') {
    state.n = 1; state.validRefresh = new Set(['rt-1']); state.validAccess = new Set(['at-1']); state.revokedAccess = new Set();
    state.expiresIn = +(u.searchParams.get('expiresIn') || 28800);
    state.rotate = u.searchParams.get('rotate') !== '0';
  }
  if (u.pathname === '/mint') { const rt = u.searchParams.get('rt'); if (!state.validRefresh.has(rt)) return res.end('{}'); const x = issue(rt); log({ ev: 'refresh', rt, ok: true, newAt: x.at, newRt: x.rt, by: 'hub' }); return res.end(JSON.stringify(x)); }
  if (u.pathname === '/revoke') state.revokedAccess.add(u.searchParams.get('at'));
  res.end(JSON.stringify({ n: state.n, validRefresh: [...state.validRefresh], validAccess: [...state.validAccess], rotate: state.rotate, expiresIn: state.expiresIn }));
}).listen(ADMIN_PORT, '127.0.0.1');
console.log('mock up', PROXY_PORT, ADMIN_PORT);
