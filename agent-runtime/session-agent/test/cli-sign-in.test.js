'use strict';
const test = require('node:test');
const assert = require('node:assert/strict');
const { detectSignIn } = require('../../common/cli-sign-in');
const { createSignInPrivacy } = require('../../common/sign-in-privacy');
const screens = {
 code: ['Login', "Browser didn't open? Use the url below to sign in (c to copy)", 'https://example.com/oauth?', 'state=example&code_challenge=example', 'Paste code here if prompted >', 'Esc to cancel'],
 device: ['Follow these steps to sign in with your account using device code authorization:', '1. Open this link in your browser and sign in to your account', 'https://example.com/device', '2. Enter this one-time code (expires in 15 minutes)', 'ABCD-12345'],
 browser: ['Starting login process...', 'Waiting for browser authentication...', 'Open a browser and navigate to this link: https://example.com/login?', 'challenge=example&mode=login', 'Press q to show a QR code to log in from another device.'],
 credential: ['Add credential', 'Create an api key at https://example.com/keys', 'Enter your API key'],
 token: ['Token provider', 'Paste Agent setup-token'],
 cursorSuccess: ['Starting login process...', 'Authenticating with Cursor...', '✓ Login successful!', 'Authentication tokens stored securely.'],
 tokenSaved: ['Token provider', 'Paste Agent setup-token', 'Auth profile: example:default (example/token)'],
 success: ['Login', 'Logged in as person@example.com', 'Login successful. Press Enter to continue…'],
 saved: ['Add credential', 'Enter your API key', '└  Done'],
 cancelled: ['Token provider', 'Paste Agent setup-token', 'Setup cancelled.'],
 failure: ['Login', 'OAuth error: Invalid code. Please make sure the full code was copied', 'Press Enter to retry.', 'Esc to cancel']
};
test('runtime and frontend recognize the measured screens identically', async () => {
 const frontend = await import('../../../frontend/src/lib/cli-sign-in.js');
 for (const rows of Object.values(screens)) assert.deepEqual(detectSignIn(rows), frontend.detectSignIn(rows));
 assert.equal(detectSignIn(screens.code).url, 'https://example.com/oauth?state=example&code_challenge=example');
 assert.equal(detectSignIn(screens.device).code, 'ABCD-12345');
 assert.equal(detectSignIn(screens.browser).mode, 'browser');
 assert.equal(detectSignIn(screens.failure).status, 'failed');
 assert.equal(detectSignIn(screens.success).confirm, true);
 assert.equal(detectSignIn(screens.cursorSuccess).status, 'succeeded');
 assert.equal(detectSignIn(screens.tokenSaved).status, 'saved');
 assert.equal(detectSignIn(screens.saved).status, 'saved');
 assert.equal(detectSignIn(screens.cancelled).status, 'cancelled');
 assert.equal(detectSignIn(['The agent said: Login successful']), null);
 assert.equal(detectSignIn(['Visit https://example.com to read the documentation']), null);
 assert.equal(detectSignIn(['Paste code here if prompted >']), null);
 assert.equal(detectSignIn([...screens.code.slice(0, 2), 'https://user:password@example.com/login', ...screens.code.slice(-2)]).url, '');
});
test('auth redraws and fragmented input never reach exported scrollback or transcripts', async () => {
 const privacy = createSignInPrivacy();
 privacy.feed('Earlier answer\r\n');
 await privacy.flush();
 privacy.feed('\x1b[2J\x1b[H' + screens.code.join('\r\n'));
 await privacy.flush();
 privacy.feed('\x1b[2J\x1b[H');
 await privacy.flush();
 privacy.feed('https://example.com/oauth?state=example&code_challenge=example');
 await privacy.flush();
 privacy.personInput('\x1b[200~example-authorization-code\x1b[201~');
 privacy.personInput('\r');
 privacy.feed('example-author');
 privacy.feed('ization-code');
 await privacy.flush();
 assert.equal(privacy.active(), true);
 const exported = privacy.scrollback();
 assert.match(exported, /Earlier answer/);
 assert.doesNotMatch(exported, /example.com|state=|authorization-code/);
 assert.doesNotMatch(privacy.mask(JSON.stringify({text:'https://example.com/oauth?state=example&code_challenge=example example-authorization-code'})), /example.com|authorization-code/);
 privacy.feed('\x1b[2J\x1b[H❯ ');
 await privacy.flush();
 privacy.feed('\r\nLater answer');
 await privacy.flush();
 assert.match(privacy.scrollback(), /Later answer/);
 privacy.dispose();
});
test('archives fail closed while auth is visible and when native diagnostics retain its data', async () => {
 const fs = require('node:fs'), os = require('node:os'), path = require('node:path');
 const dir=fs.mkdtempSync(path.join(os.tmpdir(),'signin-archive-test-'));
 const privacy=createSignInPrivacy();
 try {
  privacy.feed(screens.device.join('\r\n')); await privacy.flush();
  assert.equal(privacy.archiveSafe(dir), false);
  privacy.feed('\x1b[2J\x1b[H❯ '); await privacy.flush();
  fs.writeFileSync(path.join(dir,'native.log'), 'prefix ABCD-12345 suffix');
  assert.equal(privacy.archiveSafe(dir), false);
  fs.writeFileSync(path.join(dir,'native.log'), 'safe native data');
  assert.equal(privacy.archiveSafe(dir), true);
 } finally {privacy.dispose();fs.rmSync(dir,{recursive:true,force:true});}
});

test('archive scans refuse a file replaced between metadata and open', async () => {
 const fs=require('node:fs'),os=require('node:os'),path=require('node:path');
 const dir=fs.mkdtempSync(path.join(os.tmpdir(),'signin-file-race-'));
 const privacy=createSignInPrivacy();
 try {
  privacy.feed(screens.device.join('\r\n')); await privacy.flush();
  privacy.feed('\x1b[2J\x1b[H❯ '); await privacy.flush();
  const file=path.join(dir,'native.log');fs.writeFileSync(file,'safe');
  const racing={...fs,lstatSync(name){const stat=fs.lstatSync(name);if(name===file){fs.writeFileSync(file+'.replacement','replacement');fs.renameSync(file+'.replacement',file);}return stat;}};
  assert.equal(privacy.archiveSafe(dir,racing),false);
 } finally {privacy.dispose();fs.rmSync(dir,{recursive:true,force:true});}
});

test('values are released independently only after every export source is clean', async () => {
 const fs = require('node:fs'), os = require('node:os'), path = require('node:path');
 const dir = fs.mkdtempSync(path.join(os.tmpdir(), 'signin-release-'));
 const privacy = createSignInPrivacy();
 try {
  const state = path.join(dir, 'state'), external = path.join(dir, 'native.jsonl');
  fs.mkdirSync(state);
  privacy.feed(screens.device.join('\r\n')); await privacy.flush();
  privacy.feed('\x1b[2J\x1b[H❯ '); await privacy.flush();
  fs.writeFileSync(external, 'ABCD-12345');
  privacy.releaseAbsent([{ root: state }, { root: external }]);
  assert.equal(privacy.mask('ABCD-12345'), '[sign-in data removed]');
  assert.equal(privacy.mask('https://example.com/device'), 'https://example.com/device');
  fs.writeFileSync(external, 'clean history');
  const unreadable = { ...fs, openSync() { throw new Error('unreadable'); } };
  assert.equal(privacy.releaseAbsent([{ root: state }, { root: external }], [], unreadable), 0);
  assert.equal(privacy.mask('ABCD-12345'), '[sign-in data removed]');
  privacy.releaseAbsent([{ root: state }, { root: external }], ['ABCD-12345']);
  assert.equal(privacy.retained(), 1);
  privacy.releaseAbsent([{ root: state }, { root: external }]);
  assert.equal(privacy.retained(), 0);
 } finally { privacy.dispose(); fs.rmSync(dir, { recursive: true, force: true }); }
});

test('a real prompt ends stale login introductions without releasing blank redraws', async () => {
 const privacy = createSignInPrivacy();
 try {
  privacy.feed('Select login method:'); await privacy.flush();
  assert.equal(privacy.active(), true);
  privacy.feed('\x1b[2J\x1b[H'); await privacy.flush();
  assert.equal(privacy.active(), true);
  privacy.feed('Select login method:\r\n❯ '); await privacy.flush();
  assert.equal(privacy.active(), false);
 } finally { privacy.dispose(); }
});

test('public wrapped route words do not quarantine ordinary command metadata', async () => {
 const privacy = createSignInPrivacy();
 try {
  const rows = [...screens.code.slice(0, 2), 'https://example.com/', 'login', ...screens.code.slice(-2)];
  privacy.feed(rows.join('\r\n')); await privacy.flush();
  assert.equal(privacy.mask('login'), 'login');
  assert.equal(privacy.mask('https://example.com/login'), '[sign-in data removed]');
 } finally { privacy.dispose(); }
});

test('query values and wrapped device codes survive the public-fragment filter', async () => {
 const privacy = createSignInPrivacy();
 try {
  const url = 'https://example.com/login?state=short&code=abcde&code_challenge=opaque123456';
  privacy.feed([...screens.code.slice(0, 2), url, ...screens.code.slice(-2)].join('\r\n')); await privacy.flush();
  for (const value of [url, 'short', 'abcde', 'opaque123456']) assert.equal(privacy.mask(value), '[sign-in data removed]');
  const rows = [...screens.device.slice(0, -1), 'ABCD-', '12345'];
  assert.equal(detectSignIn(rows).code, 'ABCD-12345');
  const frontend = await import('../../../frontend/src/lib/cli-sign-in.js');
  assert.deepEqual(frontend.detectSignIn(rows), detectSignIn(rows));
  privacy.feed('\x1b[2J\x1b[H' + rows.join('\r\n')); await privacy.flush();
  for (const value of ['ABCD-12345', 'ABCD-', '12345']) assert.equal(privacy.mask(value), '[sign-in data removed]');
 } finally { privacy.dispose(); }
});

test('authorization code-mode boolean does not quarantine native JSON booleans', async () => {
 const privacy = createSignInPrivacy();
 try {
  privacy.feed([...screens.code.slice(0, 2), 'https://example.com/oauth/authorize?code=true&state=private-state-value', ...screens.code.slice(-2)].join('\r\n')); await privacy.flush();
  assert.equal(privacy.mask('true'), 'true');
  assert.equal(privacy.mask('private-state-value'), '[sign-in data removed]');
 } finally { privacy.dispose(); }
});

test('unhyphenated eight-letter device codes remain private, including wrapped rows', async () => {
 const frontend = await import('../../../frontend/src/lib/cli-sign-in.js');
 for (const codeRows of [['ABCDEFGH'], ['ABCD', 'EFGH']]) {
  const privacy = createSignInPrivacy();
  try {
   const rows = [...screens.device.slice(0, -1), ...codeRows];
   assert.equal(detectSignIn(rows).code, 'ABCDEFGH');
   assert.deepEqual(frontend.detectSignIn(rows), detectSignIn(rows));
   privacy.feed(rows.join('\r\n')); await privacy.flush();
   assert.equal(privacy.mask('ABCDEFGH'), '[sign-in data removed]');
  } finally { privacy.dispose(); }
 }
});

test('only a true code-mode flag is excluded, never other code values', async () => {
 for (const value of ['true', 'TRUE', 'false', 'actual-code']) {
  const privacy = createSignInPrivacy();
  try {
   privacy.feed([...screens.code.slice(0, 2), 'https://example.com/oauth/authorize?code=' + value, ...screens.code.slice(-2)].join('\r\n')); await privacy.flush();
   assert.equal(privacy.mask(value), /^true$/i.test(value) ? value : '[sign-in data removed]');
  } finally { privacy.dispose(); }
 }
});
