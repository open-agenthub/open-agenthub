import assert from 'node:assert/strict';
import { execFileSync, spawn } from 'node:child_process';
import { mkdtempSync, readFileSync, writeFileSync, statSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { randomBytes } from 'node:crypto';
if (process.env.GITHUB_ACTIONS !== 'true' && process.env.SUPPORT_BUNDLE_DISPOSABLE !== '1')
  throw Error('Use only a disposable quickstart cluster; explicitly set SUPPORT_BUNDLE_DISPOSABLE=1');
const output = mkdtempSync(join(tmpdir(), 'support-bundle-proof-'));
const run = (cmd, args, input) => execFileSync(cmd, args, { input, encoding: 'utf8', stdio: ['pipe', 'pipe', 'pipe'], timeout: 240000 });
const kube = (...args) => run('kubectl', ['-n', 'agenthub', ...args]);
const obj = (...args) => JSON.parse(kube(...args, '-o', 'json'));
const sql = input => kubeInput(['exec', '-i', 'postgres-0', '-c', 'postgres', '--', 'psql', '-U', 'agenthub', '-d', 'agenthub', '-v', 'ON_ERROR_STOP=1', '-At'], input);
const kubeInput = (args, input) => run('kubectl', ['-n', 'agenthub', ...args], input);
const canaries = [];
const canary = label => { const value = label + '-' + randomBytes(24).toString('hex'); canaries.push(value); return value; };
const key = Buffer.from(obj('get', 'secret', 'agenthub-api-key').data['api-key'], 'base64').toString();
canaries.push(key);
for (const name of ['agenthub-secrets', 'postgres-secret'])
  for (const value of Object.values(obj('get', 'secret', name).data)) {
    const decoded = Buffer.from(value, 'base64').toString();
    canaries.push(decoded);
    // Also scan individual connection-string passwords rather than only the whole string.
    const password = decoded.match(/Password=([^;]+)/i)?.[1];
    if (password) canaries.push(password);
  }
let forward, issuer, session, secret;
let credential = key;
const base = 'http://127.0.0.1:18883';
const sleep = ms => new Promise(resolve => setTimeout(resolve, ms));
async function until(fn) {
  let error;
  for (let n = 0; n < 120; n++) { try { const value = await fn(); if (value) return value; } catch (e) { error = e; } await sleep(1000); }
  throw error || Error('proof wait timed out');
}
async function api(path, method = 'GET', body, expected = 200, auth = credential) {
  const response = await fetch(base + '/api/' + path, { method, signal: AbortSignal.timeout(15000),
    headers: { ...(auth ? { Authorization: 'Bearer ' + auth } : {}), 'Content-Type': 'application/json' },
    body: body === undefined ? undefined : JSON.stringify(body) });
  assert.equal(response.status, expected, method + ' ' + path);
  const text = await response.text(); return text && expected === 200 ? JSON.parse(text) : null;
}
async function connect() {
  forward?.kill();
  forward = spawn('kubectl', ['-n', 'agenthub', 'port-forward', 'service/agenthub-backend', '18883:80'], { stdio: 'ignore' });
  await until(async () => { const r = await fetch(base + '/healthz', { signal: AbortSignal.timeout(3000) }); return r.ok; });
}
const previous = JSON.parse(run('helm', ['history', 'agenthub', '-n', 'agenthub', '-o', 'json'])).findLast(x => x.status === 'deployed').revision;
function collect(label, logs) {
  const path = join(output, label + '.json');
  run('python3', ['scripts/support-bundle.py', '--namespace', 'agenthub', '--admin-url', base,
    '--output', path, ...(logs ? ['--include-backend-logs', '--log-lines', '1000'] : [])], credential + '\n');
  run('python3', ['tests/SupportBundleProof/check.py', path, '-'], JSON.stringify(canaries));
  let grepStatus = 0;
  try { run('grep', ['-F', '-f', '-', path], canaries.filter(value => value && !/[\r\n]/.test(value)).join('\n') + '\n'); } catch (e) { grepStatus = e.status; }
  assert.equal(grepStatus, 1, 'literal grep must find no canary');
  const bundle = JSON.parse(readFileSync(path));
  assert.equal(statSync(path).mode & 0o777, 0o600);
  assert.equal('backendLogs' in bundle, logs);
  if (logs) assert(bundle.logsWarning.includes('DPA'));
  return bundle;
}
const logName = canary('support-log');
try {
  // Startup logs include the configured API-key owner. A synthetic token-shaped
  // name exercises real log exclusion without weakening request-log protections.
  kube('set', 'env', 'deployment/agenthub-backend', 'Auth__ApiKeyOwner=' + logName);
  kube('rollout', 'status', 'deployment/agenthub-backend', '--timeout=180s');
  await connect();
  await api('admin/diagnostics', 'GET', undefined, 401, '');
  const license = canary('invalid-test-license');
  const instanceKey = canary('instance-key');
  sql(`INSERT INTO app_license(id, token, instance_key) VALUES (1, '${license}', '${instanceKey}') ON CONFLICT(id) DO UPDATE SET token=EXCLUDED.token, instance_key=EXCLUDED.instance_key;`);
  const prompt = canary('session-prompt'), secretValue = canary('session-secret'), title = canary('session-title');
  secret = (await api('secrets', 'POST', { name: 'SUPPORT_PROOF', delivery: 'env', value: secretValue })).id;
  // A real scheduled session creates its CronJob, prompt and secret without calling a model.
  session = (await api('sessions', 'POST', { title, prompt, secretIds: [secret], mode: 'Scheduled',
    schedule: '0 0 1 1 *', agent: 'Claude', authMode: 'CustomEndpoint',
    modelEndpointUrl: 'https://model.example.com', modelName: 'support-proof', repos: [] })).id;
  assert(session);
  const cron = JSON.parse(run('kubectl', ['-n', 'agenthub-sessions', 'get', 'cronjobs', '-o', 'json']));
  for (const job of cron.items) for (const c of job.spec.jobTemplate.spec.template.spec.containers)
    for (const e of c.env || []) if (e.name === 'AGENTHUB_CALLBACK_TOKEN' && e.value) canaries.push(e.value);
  assert(JSON.stringify(cron).includes(prompt), 'prompt must exist in real cluster resource');
  const allSecrets = JSON.parse(run('kubectl', ['-n', 'agenthub-sessions', 'get', 'secrets', '-o', 'json']));
  assert(JSON.stringify(allSecrets).includes(Buffer.from(secretValue).toString('base64')), 'session secret must reach real cluster');
  for (const s of allSecrets.items) for (const v of Object.values(s.data || {})) canaries.push(Buffer.from(v, 'base64').toString());
  const unknown = canary('unknown-schema-row');
  sql(`INSERT INTO schema_scripts(name, sha256) VALUES ('${unknown}', 'not-a-real-script');`);
  await until(() => kube('logs', 'deployment/agenthub-backend', '-c', 'backend', '--all-pods=true', '--tail=1000').includes(logName));
  const normal = collect('api-default', false);
  assert.equal(normal.application.license.present, true);
  assert.equal(normal.application.license.valid, false);
  collect('api-logs', true);
  console.log('PASS: API-key default and log bundles, real scheduled prompt/secret, invalid test licence, instance key, schema and raw backend log canaries; grep clean');
  // Remove the table briefly to prove diagnostics remain usable; no other schema is changed.
  sql('ALTER TABLE schema_scripts RENAME TO schema_scripts_support_proof;');
  try { const missing = await api('admin/diagnostics'); assert.equal(missing.schemaScripts, null); assert.equal(missing.schemaReason, 'unavailable'); }
  finally { sql('ALTER TABLE schema_scripts_support_proof RENAME TO schema_scripts;'); }
  await api('sessions/' + session, 'DELETE', undefined, 204); session = null;
  await api('secrets/' + secret, 'DELETE', undefined, 204); secret = null;
  const node = JSON.parse(run('kubectl', ['get', 'nodes', '-o', 'json'])).items[0].status.addresses.find(x => x.type === 'InternalIP').address;
  const authority = 'http://' + node + ':18084';
  issuer = spawn('node', ['tests/SupportBundleProof/issuer.mjs'], { env: { ...process.env, PROOF_ISSUER: authority }, stdio: 'ignore' });
  await until(async () => (await fetch(authority + '/.well-known/openid-configuration', { signal: AbortSignal.timeout(3000) })).ok);
  const tokens = await (await fetch(authority + '/proof-tokens', { signal: AbortSignal.timeout(5000) })).json();
  canaries.push(tokens.admin, tokens.user, 'support-admin@example.com', 'support-user@example.com');
  const oidcSecret = canary('oidc-client-secret'), unknownValue = canary('unknown-helm-field');
  const valuesPath = join(output, 'values.json');
  writeFileSync(valuesPath, JSON.stringify({ oidc: { authority, requireHttpsMetadata: false, clientId: 'agenthub', audience: 'agenthub', clientSecret: oidcSecret }, ee: { admins: 'support-admin' }, unknownSupportField: unknownValue }));
  // The public-client chart has no OIDC client secret; seed an ignored value AND a real Secret.
  kubeInput(['apply', '-f', '-'], JSON.stringify({ apiVersion: 'v1', kind: 'Secret', metadata: { name: 'support-proof-oidc' }, stringData: { clientSecret: oidcSecret } }));
  run('helm', ['upgrade', 'agenthub', './helm/open-agenthub', '-n', 'agenthub', '--reuse-values', '-f', valuesPath, '--wait', '--timeout', '3m']);
  credential = tokens.admin;
  await connect();
  await api('admin/diagnostics', 'GET', undefined, 403, tokens.user);
  const oidc = collect('oidc-default', false);
  assert.equal(oidc.application.oidc.authorityPresent, true);
  collect('oidc-logs', true);
  console.log('PASS: real OIDC administrator 200 / non-admin 403 / unauthenticated 401; default and opt-in bundles exclude all credentials and user content');
  // A new unknown value must turn the same full-output leak check red.
  const negative = join(output, 'negative.json');
  writeFileSync(negative, JSON.stringify({ newField: unknownValue }));
  let rejected = false;
  try { run('python3', ['tests/SupportBundleProof/check.py', negative, '-'], JSON.stringify(canaries)); } catch { rejected = true; }
  assert(rejected);
  console.log('PASS: new-value leak negative control rejected; missing schema table returns null/unavailable');
} finally {
  if (session) { try { await api('sessions/' + session, 'DELETE', undefined, 204); } catch {} }
  if (secret) { try { await api('secrets/' + secret, 'DELETE', undefined, 204); } catch {} }
  forward?.kill();
  try { run('helm', ['rollback', 'agenthub', String(previous), '-n', 'agenthub', '--wait', '--timeout', '3m']); } finally { issuer?.kill(); }
  sql("UPDATE app_license SET token=NULL, instance_key=NULL WHERE id=1; DELETE FROM schema_scripts WHERE sha256='not-a-real-script';");
  kube('delete', 'secret', 'support-proof-oidc', '--ignore-not-found');
}
