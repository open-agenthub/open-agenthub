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
