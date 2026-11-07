// Drives production sharing/access controllers against real Postgres and the running native
// OpenCode PTY transport. Synthetic credentials deliberately appear in native model output.
import assert from 'node:assert/strict';
import fs from 'node:fs';
const base = process.env.PROOF_URL || 'http://127.0.0.1:8080';
const request = async (route, { user, method = 'GET', body } = {}) => {
  const response = await fetch(base + route, { method, headers: { ...(user ? { cookie: 'proof-user=' + user } : {}), 'content-type': 'application/json' }, body: body ? JSON.stringify(body) : undefined });
  return response;
};
const owner = { user: 'alice' };
const share = await request('/api/ee/sessions/reviews/shares/links', { ...owner, method: 'POST', body: { role: 'Viewer', expiresAt: null } });
if (!share.ok) throw new Error(await share.text());
const link = await share.json();
const token = link.url.split('/').at(-1);
const named = await request('/api/ee/sessions/reviews/shares/users', { ...owner, method: 'POST', body: { recipient: 'bob', role: 'Viewer' } });
assert(named.ok, await named.text());
const live = await (await request('/api/proof/live')).json();
assert(live.liveEntries > 200, JSON.stringify(live));
async function history(route, options) {
  const entries = [];
  let offset = 0;
  for (let pages = 0; pages < 20; pages++) {
    const response = await request(route + `?offset=${offset}&max=40`, options);
    assert(response.ok, String(response.status));
    const page = await response.json();
    entries.push(...page.entries);
    if (page.nextOffset >= page.length) return { entries, pages: pages + 1 };
    assert(page.nextOffset > offset); offset = page.nextOffset;
  }
  throw new Error('history paging did not terminate');
}
const linkRead = await history(`/api/shared/${token}/conversation`);
const namedRead = await history('/api/sessions/reviews/conversation', { user: 'bob' });
for (const { entries } of [linkRead, namedRead]) {
  const text = JSON.stringify(entries);
  assert(text.includes('first native turn'));
  assert(text.includes('Native turn 109'));
  assert(entries.some(entry => entry.role === 'tool' && entry.tool === 'bash'));
  assert(entries.some(entry => entry.role === 'result' && entry.text.includes('native-tool-success')));
  assert(!text.includes('callback-token-reviews-7f3c9e'));
  assert(!text.includes('mcp-owner-credential-5b1d2a'));
  assert(text.includes('[redacted]'));
}
const deleted = await request('/api/ee/sessions/reviews/shares/links/' + link.link.id, { ...owner, method: 'DELETE' });
assert(deleted.ok);
assert.equal((await request(`/api/shared/${token}/conversation?offset=0`)).status, 404);
assert((await request('/api/ee/sessions/reviews/shares/users/bob', { ...owner, method: 'DELETE' })).ok);
assert.equal((await request('/api/sessions/reviews/conversation?offset=0', { user: 'bob' })).status, 404);
const results = { sharedEntries: linkRead.entries.length, pages: linkRead.pages, namedEntries: namedRead.entries.length, masking: true, revocation: true, liveEntries: live.liveEntries };
console.log(JSON.stringify(results, null, 2));
if (process.env.PROOF_OUT) fs.writeFileSync(process.env.PROOF_OUT + '/shared-results.json', JSON.stringify(results, null, 2));
