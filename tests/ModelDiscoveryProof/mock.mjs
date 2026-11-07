// Recording mock for model-list probes. Per caller (chatgpt-account-id, x-api-key or bearer
// suffix) it answers from rules.json: { "<key>": { "status": 403 } | { "ids": [...] } }.
// Logs only the first 12 characters of a credential, so the log itself never holds one.
import http from 'node:http';
import fs from 'node:fs';
const [port, log, rulesFile, bundled] = process.argv.slice(2);
const codexModels = bundled ? JSON.parse(fs.readFileSync(bundled, 'utf8')).models : [];
http.createServer((req, res) => {
  let body = ''; req.on('data', d => body += d); req.on('end', () => {
    const rules = JSON.parse(fs.readFileSync(rulesFile, 'utf8'));
    const cred = req.headers['chatgpt-account-id'] || req.headers['x-api-key'] || (req.headers.authorization || '').replace(/^Bearer /, '');
    const key = Object.keys(rules).find(k => cred.includes(k)) || 'default';
    const rule = rules[key] || {};
    fs.appendFileSync(log, JSON.stringify({ m: req.method, u: req.url, who: key, cred: cred.slice(0, 12) }) + '\n');
    const json = (s, o) => { res.writeHead(s, { 'content-type': 'application/json' }); res.end(JSON.stringify(o)); };
    if (!/models/.test(req.url)) return json(404, {});
    if (rule.status) return json(rule.status, { error: { type: 'permission_error', message: 'This account is not entitled to list models' } });
    const ids = rule.ids || [];
    if (req.url.includes('/codex/models')) {
      const pick = codexModels.filter(m => ids.includes(m.slug));
      return json(200, { models: pick });
    }
    // Anthropic- and OpenAI-shaped list in one body: each client reads its own field.
    return json(200, { object: 'list', has_more: false, first_id: ids[0], last_id: ids.at(-1),
      data: ids.map(id => ({ id, object: 'model', type: 'model', display_name: id.toUpperCase(), created: 1, created_at: '2026-01-01T00:00:00Z', owned_by: 'mock' })) });
  });
}).listen(+port, '127.0.0.1');
