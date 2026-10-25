// Recording mock model for the #258 usage proofs. Answers the Anthropic Messages API (Claude Code),
// the OpenAI Responses API (Codex) and Chat Completions (OpenCode/OpenClaw) with FIXED, distinct
// usage numbers, so every count the hub ends up storing can be traced back to one response here.
// It also records raw OTLP/HTTP metric bodies posted to /v1/metrics (Claude Code's exporter).
//   node mock-model.mjs <port> <outdir>
import http from 'node:http';
import fs from 'node:fs';

const port = Number(process.argv[2] || 18780);
const out = process.argv[3] || '.';
fs.mkdirSync(out, { recursive: true });
let n = 0;
const log = o => fs.appendFileSync(`${out}/model.log`, JSON.stringify({ at: new Date().toISOString(), ...o }) + '\n');

// Per response: input 1000 (of which 600 cached), output 50 (of which 20 reasoning).
export const USAGE = { input: 1000, cached: 600, output: 50, reasoning: 20, cacheWrite: 300 };

http.createServer((req, res) => {
  const chunks = [];
  req.on('data', c => chunks.push(c));
  req.on('end', () => {
    const raw = Buffer.concat(chunks);
    const url = new URL(req.url, 'http://x');
    if (url.pathname.endsWith('/v1/metrics')) {
      fs.writeFileSync(`${out}/otlp-${Date.now()}-${++n}.bin`, raw);
      log({ path: url.pathname, otlpBytes: raw.length });
      res.writeHead(200, { 'content-type': 'application/x-protobuf' }); res.end();
      return;
    }
    let body = {};
    try { body = JSON.parse(raw.toString() || '{}'); } catch { /* not json */ }
    // Which credential KIND the CLI sent (never the value): shows which login a run used.
    const auth = req.headers['x-api-key'] ? 'x-api-key'
      : /^Bearer sk-ant-oat/.test(req.headers.authorization || '') ? 'oauth-bearer'
      : req.headers.authorization ? 'bearer' : 'none';
    log({ method: req.method, path: url.pathname, model: body.model, stream: body.stream, auth });

    if (url.pathname.startsWith('/v1/messages')) {
      if (url.pathname.includes('count_tokens') || !body.stream) {
        res.writeHead(200, { 'content-type': 'application/json' });
        res.end(JSON.stringify({ input_tokens: 10, id: 'x', type: 'message', role: 'assistant', model: body.model || 'm', content: [{ type: 'text', text: 'ok' }], stop_reason: 'end_turn', usage: { input_tokens: 1, output_tokens: 1 } }));
        return;
      }
      res.writeHead(200, { 'content-type': 'text/event-stream' });
      const ev = (type, data) => res.write(`event: ${type}\ndata: ${JSON.stringify({ type, ...data })}\n\n`);
      ev('message_start', { message: { id: 'msg_' + (++n), type: 'message', role: 'assistant', model: body.model || 'claude-sonnet-4-5', content: [], stop_reason: null, stop_sequence: null,
        usage: { input_tokens: USAGE.input - USAGE.cached, cache_read_input_tokens: USAGE.cached, cache_creation_input_tokens: USAGE.cacheWrite, output_tokens: 1 } } });
      ev('content_block_start', { index: 0, content_block: { type: 'text', text: '' } });
      ev('content_block_delta', { index: 0, delta: { type: 'text_delta', text: 'usage rig ok' } });
      ev('content_block_stop', { index: 0 });
      ev('message_delta', { delta: { stop_reason: 'end_turn', stop_sequence: null }, usage: { output_tokens: USAGE.output } });
      ev('message_stop', {});
      res.end();
      return;
    }
    if (url.pathname.endsWith('/responses') && req.method === 'POST') {
      const id = 'r' + (++n);
      const sse = events => events.map(e => `event: ${e.type}\ndata: ${JSON.stringify(e)}\n\n`).join('');
      res.writeHead(200, { 'content-type': 'text/event-stream' });
      res.end(sse([
        { type: 'response.created', response: { id } },
        { type: 'response.output_item.done', item: { type: 'message', role: 'assistant', id: 'm' + id, content: [{ type: 'output_text', text: 'usage rig ok' }] } },
        { type: 'response.completed', response: { id, usage: {
          input_tokens: USAGE.input, input_tokens_details: { cached_tokens: USAGE.cached },
          output_tokens: USAGE.output, output_tokens_details: { reasoning_tokens: USAGE.reasoning },
          total_tokens: USAGE.input + USAGE.output } } }
      ]));
      return;
    }
    if (url.pathname.endsWith('/chat/completions')) {
      const usage = { prompt_tokens: USAGE.input, completion_tokens: USAGE.output, total_tokens: USAGE.input + USAGE.output,
        prompt_tokens_details: { cached_tokens: USAGE.cached }, completion_tokens_details: { reasoning_tokens: USAGE.reasoning } };
      if (!body.stream) {
        res.writeHead(200, { 'content-type': 'application/json' });
        res.end(JSON.stringify({ id: 'c', object: 'chat.completion', created: 1, model: body.model || 'm1', choices: [{ index: 0, message: { role: 'assistant', content: 'usage rig ok' }, finish_reason: 'stop' }], usage }));
        return;
      }
      res.writeHead(200, { 'content-type': 'text/event-stream' });
      const b = { id: 'c' + (++n), object: 'chat.completion.chunk', created: 1, model: body.model || 'm1' };
      const send = o => res.write('data: ' + JSON.stringify(o) + '\n\n');
      send({ ...b, choices: [{ index: 0, delta: { role: 'assistant', content: '' } }] });
      send({ ...b, choices: [{ index: 0, delta: { content: 'usage rig ok' } }] });
      send({ ...b, choices: [{ index: 0, delta: {}, finish_reason: 'stop' }] });
      send({ ...b, choices: [], usage });
      res.end('data: [DONE]\n\n');
      return;
    }
    res.writeHead(200, { 'content-type': 'application/json' });
    res.end('{}');
  });
}).listen(port, '127.0.0.1', () => console.log('mock model on', port));
