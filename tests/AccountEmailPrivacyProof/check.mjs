import fs from 'node:fs';
import assert from 'node:assert/strict';
import path from 'node:path';
import { createRequire } from 'node:module';
const root = process.env.RIG_HOME;
const require = createRequire(process.env.RUNTIME + '/package.json');
const { detectMenu } = require('./common/cli-menu');
const interactive = fs.readFileSync(path.join(root, 'interactive/screen.txt'), 'utf8');
assert.match(interactive, /account email address/);
const menu = detectMenu(interactive.split('\n'));
assert.equal(menu?.partial, false);
assert.deepEqual(menu.options.map(x => x.label), ['Yes', 'No']);
const auto = fs.readFileSync(path.join(root, 'auto/screen.txt'), 'utf8');
assert.match(auto, /declined automatically in an unattended session/);
assert.equal(detectMenu(auto.split('\n')), null);
const home = fs.readFileSync(path.join(root, 'autonomous/home'), 'utf8');
let refused = false;
function scan(dir) { for (const e of fs.readdirSync(dir, { withFileTypes: true })) { const p = path.join(dir, e.name); if(e.isDirectory())scan(p);else if(p.endsWith('.jsonl')) refused ||= fs.readFileSync(p,'utf8').includes('Sending the account email was declined automatically in an unattended session.'); } }
scan(path.join(home, '.claude/projects'));
assert.equal(refused, true);
assert.match(fs.readFileSync(path.join(root, 'autonomous/screen.txt'), 'utf8'), /ok/);
for (const name of ['auto', 'autonomous']) {
 const callbacks = fs.readFileSync(path.join(root, name, 'model.log'), 'utf8').trim().split('\n').map(JSON.parse);
 assert.ok(callbacks.length >= 2);
 assert.ok(callbacks.every(event => event.emailLeaked === false));
 assert.ok(callbacks.some(event => event.privacy));
 assert.ok(callbacks.some(event => event.commandRetained && event.redacted));
}
const events=fs.readFileSync(path.join(root,'model/events.jsonl'),'utf8');
assert.doesNotMatch(events,/email-receiver-called/);
assert.match(events,/"privacyRefusal":true/);
console.log('PASS: native privacy prompt, interactive detection, autoApprove refusal, unattended completion, no disclosure, redacted callbacks');
