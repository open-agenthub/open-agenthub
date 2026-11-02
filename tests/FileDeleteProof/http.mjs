// Deleting and cancelling over the proof host's real controllers, Postgres and object storage.
// Each role's answer is checked, and "gone" is read from the bucket itself (/__objects).
import assert from 'node:assert/strict';

const base = process.env.PROOF_URL || 'http://127.0.0.1:8080';
const as = user => fetch(`${base}/__as/${user}`, { method: 'POST' });
const objects = async () => (await (await fetch(`${base}/__objects`)).json());
const call = (method, path, body) => fetch(base + path, {
  method, headers: body ? { 'content-type': 'application/json' } : {},
  body: body ? JSON.stringify(body) : undefined
});

async function reserve(user, name) {
  await as(user);
  const res = await call('POST', '/api/sessions/s1/files/reserve', { name, mimeType: 'text/plain', size: 5 });
  assert.equal(res.status, 200, `reserve ${name} as ${user}`);
  return (await res.json()).file.id;
}

async function upload(user, name) {
  const id = await reserve(user, name);
  assert.equal((await fetch(`${base}/api/sessions/s1/files/${id}/content`, { method: 'PUT', body: 'hello' })).status, 204);
  assert.equal((await call('POST', `/api/sessions/s1/files/${id}/complete`)).status, 200);
  return id;
}

const stored = async id => (await objects()).some(key => key.includes(id));
const status = async (user, method, path) => { if (user) await as(user); return (await call(method, path)).status; };

await (await fetch(`${base}/__direct/bob/collaborator`, { method: 'POST' })).json();
await (await fetch(`${base}/__direct/carol/viewer`, { method: 'POST' })).json();
const viewerLink = (await (await fetch(`${base}/__share/viewer`, { method: 'POST' })).json()).token;
const collaboratorLink = (await (await fetch(`${base}/__share/collaborator`, { method: 'POST' })).json()).token;

// Completed files: owner and collaborator may delete, the viewer and both links may not.
const first = await upload('alice', 'first.txt');
const second = await upload('bob', 'second.txt');
assert.ok(await stored(first) && await stored(second), 'both uploads are in the bucket');
assert.equal(await status('carol', 'DELETE', `/api/sessions/s1/files/${first}`), 403);
assert.equal(await status('dave', 'DELETE', `/api/sessions/s1/files/${first}`), 404);
assert.equal(await status(null, 'DELETE', `/api/shared/${viewerLink}/files/${first}`), 404);
assert.equal(await status(null, 'DELETE', `/api/shared/${collaboratorLink}/files/${first}`), 404);
assert.ok(await stored(first), 'refused deletes leave the object');
console.log('PASS viewer 403, unshared user 404, viewer and collaborator links have no delete route; object kept');

assert.equal(await status('bob', 'DELETE', `/api/sessions/s1/files/${first}`), 204);
assert.equal(await status('alice', 'DELETE', `/api/sessions/s1/files/${second}`), 204);
assert.ok(!(await stored(first)) && !(await stored(second)), 'deleted objects are gone from the bucket');
await as('alice');
const listed = await (await call('GET', '/api/sessions/s1/files')).json();
assert.ok(!listed.some(file => file.id === first || file.id === second), 'deleted files are not listed');
console.log('PASS collaborator and owner delete completed files: 204, unlisted, objects gone from storage');

// The two kinds do not stand in for each other.
const pending = await reserve('bob', 'pending.txt');
const done = await upload('bob', 'done.txt');
await as('bob');
let res = await call('DELETE', `/api/sessions/s1/files/${pending}`);
assert.deepEqual([res.status, (await res.json()).code], [409, 'file_upload_pending']);
res = await call('DELETE', `/api/sessions/s1/files/${done}/upload`);
assert.deepEqual([res.status, (await res.json()).code], [409, 'file_upload_complete']);
assert.ok(await stored(done), 'a refused cancel leaves the completed file');
console.log('PASS delete refuses an upload in flight (409 file_upload_pending), cancel refuses a completed file (409 file_upload_complete)');

// Cancelling: only the uploader or the owner.
const alices = await reserve('alice', 'alices.txt');
assert.equal(await status('bob', 'DELETE', `/api/sessions/s1/files/${alices}/upload`), 403);
assert.equal(await status('carol', 'DELETE', `/api/sessions/s1/files/${pending}/upload`), 403);
assert.equal(await status('bob', 'DELETE', `/api/sessions/s1/files/${pending}/upload`), 204);
assert.equal(await status('alice', 'DELETE', `/api/sessions/s1/files/${alices}/upload`), 204);
assert.equal(await status('bob', 'POST', `/api/sessions/s1/files/${pending}/complete`), 409);
console.log('PASS cancel: another collaborator 403, viewer 403, uploader 204, owner 204; a cancelled upload cannot complete');

assert.equal(await status('alice', 'DELETE', `/api/sessions/s1/files/${done}`), 204);
assert.ok(!(await stored(done)));
await as('alice');
console.log('PASS all proof objects removed:', (await objects()).filter(key => [first, second, done].some(id => key.includes(id))).length === 0);
