import assert from 'node:assert/strict';
import { execFileSync, spawn } from 'node:child_process';
const control = 'agenthub', ns = 'agenthub-sessions';
const kubectl = (...args) => execFileSync('kubectl', args, { encoding: 'utf8', stdio: ['pipe', 'pipe', 'pipe'] });
const json = (...args) => JSON.parse(kubectl(...args, '-o', 'json'));
const key = Buffer.from(json('-n', control, 'get', 'secret', 'agenthub-api-key').data['api-key'], 'base64').toString();
const sleep = ms => new Promise(resolve => setTimeout(resolve, ms));
async function until(fn) {
  let error;
  for (let n = 0; n < 240; n++) { try { const result = await fn(); if (result) return result; } catch (caught) { error = caught; } await sleep(500); }
  throw error ?? new Error('proof timed out');
}
async function api(route, method = 'GET', body, expected = 200) {
  const response = await fetch('http://127.0.0.1:18880/api/' + route, { method,
    headers: { Authorization: 'Bearer ' + key, 'Content-Type': 'application/json' },
    body: body === undefined ? undefined : JSON.stringify(body) });
  assert.equal(response.status, expected, method + ' ' + route + ': ' + await response.clone().text());
  const text = await response.text(); return text ? JSON.parse(text) : null;
}
let id, claim, replacementForward;
try {
  await until(async () => { await api('credentials'); return true; });
  await api('credentials', 'PUT', { anthropicApiKey: 'synthetic-workspace-proof' }, 204);
  const created = await api('sessions', 'POST', { title: 'Persistent workspace proof', agent: 'Claude', authMode: 'ApiKey', mode: 'Interactive', repos: [] });
  id = created.id;
  const podName = 'session-' + id;
  const pod = () => json('-n', ns, 'get', 'pod', podName);
  const ready = () => until(() => { const current = pod(); return current.status.conditions?.some(item => item.type === 'Ready' && item.status === 'True') && current; });
  const first = await ready();
  claim = first.spec.volumes.find(item => item.name === 'workspace').persistentVolumeClaim.claimName;
  const volume = json('-n', ns, 'get', 'pvc', claim);
  assert.equal(volume.status.phase, 'Bound');
  assert.equal(volume.spec.resources.requests.storage, '64Mi');
  assert(!volume.metadata.ownerReferences?.some(item => item.kind === 'Pod'));
  const run = script => kubectl('-n', ns, 'exec', podName, '-c', 'agent', '--', '/bin/sh', '-ec', script);
  run(`for name in first second; do
    mkdir -p /workspace/$name
    cd /workspace/$name
    git init -q
    git config user.name Proof
    git config user.email proof@example.com
    printf 'tracked baseline\\n' > tracked
    git add tracked
    git commit -qm baseline
    printf 'dirty contents\\n' >> tracked
    printf 'untracked contents\\n' > untracked
  done`);
  const fingerprint = () => run('cd /workspace; find . -type f -exec sha256sum {} \\; | sort');
  const before = fingerprint();
  await api('sessions/' + id + '/pause', 'POST');
  await until(() => { try { pod(); return false; } catch { return true; } });
  assert.equal(json('-n', ns, 'get', 'pvc', claim).metadata.uid, volume.metadata.uid);
  await api('sessions/' + id + '/resume', 'POST');
  const second = await ready();
  assert.notEqual(second.metadata.uid, first.metadata.uid);
  assert.equal(fingerprint(), before);
  assert.equal(json('-n', ns, 'get', 'pvc', claim).metadata.uid, volume.metadata.uid);
  // Turning admission off must not strand an already recorded claim on emptyDir.
  kubectl('-n', control, 'set', 'env', 'deployment/agenthub-backend', 'AgentHub__WorkspaceVolumePersistent=false');
  kubectl('-n', control, 'rollout', 'status', 'deployment/agenthub-backend', '--timeout=180s');
  // A port-forward selects one pod and exits when that pod is replaced by this rollout.
  replacementForward = spawn('kubectl', ['-n', control, 'port-forward', 'service/agenthub-backend', '18880:80'], { stdio: 'ignore' });
  await until(async () => { await api('credentials'); return true; });
  await api('sessions/' + id + '/pause', 'POST');
  await until(() => { try { pod(); return false; } catch { return true; } });
  await api('sessions/' + id + '/resume', 'POST');
  await ready();
  assert.equal(fingerprint(), before);
  await api('sessions/' + id, 'DELETE', undefined, 204);
  await until(() => { try { json('-n', ns, 'get', 'pvc', claim); return false; } catch { return true; } });
  await api('sessions/' + id, 'GET', undefined, 404);
  id = null;
  console.log('PASS: actual backend, kubelet and local-path PVC; dirty two-repository bytes and Git metadata survive pause/resume and flag disable; new pod UID, same claim UID, central deletion removes claim.');
} catch (error) {
  if (id) {
    try { console.error('Session pod status:', JSON.stringify(json('-n', ns, 'get', 'pod', 'session-' + id).status)); } catch {}
    for (const container of ['prepare-tmp', 'git-clone', 'agent']) {
      try { console.error(container + ':', kubectl('-n', ns, 'logs', 'session-' + id, '-c', container, '--tail=40')); } catch {}
    }
    try { console.error('Claim statuses:', JSON.stringify(json('-n', ns, 'get', 'pvc').items.map(item => ({ name: item.metadata.name, status: item.status })))); } catch {}
  }
  throw error;
} finally {
  if (id) { try { await api('sessions/' + id, 'DELETE', undefined, 204); } catch { console.error('Proof session cleanup failed; inspect retained registry and PVC before deleting the disposable cluster.'); } }
  replacementForward?.kill('SIGTERM');
}
