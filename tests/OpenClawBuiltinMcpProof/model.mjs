// Mock OpenAI Chat Completions model. Turn 1: logs the tool names OpenClaw offered and calls the
// first tool whose name ends in agent_send. Turn 2 (a tool result is present): answers "sent".
import http from 'node:http';

const PORT = Number(process.env.MODEL_PORT || 18600);
http.createServer((req, res) => {
  let body = '';
  req.on('data', chunk => { body += chunk; });
  req.on('end', () => {
    let json = {};
    try { json = JSON.parse(body); } catch {}
    if (req.method === 'GET') {
      res.writeHead(200, { 'content-type': 'application/json' });
      return res.end(JSON.stringify({ object: 'list', data: [{ id: 'm1', object: 'model', created: 0, owned_by: 'mock' }] }));
    }
    const tools = (json.tools || []).map(t => t.function?.name || t.name).filter(Boolean);
    console.log('[model] ' + JSON.stringify({ url: req.url, toolCount: tools.length,
      agenthubTools: tools.filter(n => /agenthub|skill/i.test(n)) }));
    const messages = json.messages || [];
    const toolResult = messages.find(m => m.role === 'tool');
    const send = tools.find(n => /agent_send$/.test(n));
    let delta;
    if (toolResult) {
      console.log('[model] tool result: ' + JSON.stringify(toolResult.content).slice(0, 400));
      delta = { role: 'assistant', content: 'sent' };
    } else if (send) {
      delta = { role: 'assistant', tool_calls: [{ index: 0, id: 'call_1', type: 'function',
        function: { name: send, arguments: JSON.stringify({ to: 'Peer', message: 'hello from openclaw' }) } }] };
    } else {
      delta = { role: 'assistant', content: 'no agent_send tool offered' };
    }
    const finish = delta.tool_calls ? 'tool_calls' : 'stop';
    const usage = { prompt_tokens: 10, completion_tokens: 1, total_tokens: 11 };
    if (!json.stream) {
      res.writeHead(200, { 'content-type': 'application/json' });
      return res.end(JSON.stringify({ id: 'c1', object: 'chat.completion', created: 0, model: json.model,
        choices: [{ index: 0, message: { ...delta, tool_calls: delta.tool_calls?.map(({ index, ...c }) => c) }, finish_reason: finish }], usage }));
    }
    res.writeHead(200, { 'content-type': 'text/event-stream' });
    const chunk = d => res.write(`data: ${JSON.stringify({ id: 'c1', object: 'chat.completion.chunk', created: 0, model: json.model, ...d })}\n\n`);
    chunk({ choices: [{ index: 0, delta, finish_reason: null }] });
    chunk({ choices: [{ index: 0, delta: {}, finish_reason: finish }], usage });
    res.write('data: [DONE]\n\n');
    res.end();
  });
}).listen(PORT, '127.0.0.1', () => console.log('[model] listening on ' + PORT));
