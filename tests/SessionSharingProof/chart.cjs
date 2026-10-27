const { execFileSync } = require('node:child_process');
const assert = require('node:assert/strict');
const helm = process.env.HELM || 'helm';
for (const [name, setting, expected] of [
 ['default', [], 'true'],
 ['disabled', ['--set', 'sharing.userSuggestionsEnabled=false'], 'false']
]) {
 const yaml = execFileSync(helm,['template','sharing-proof','helm/open-agenthub','--set','postgres.password=proof-only',...setting],{encoding:'utf8'});
 assert.ok(yaml.includes(`Sharing__UserSuggestionsEnabled: "${expected}"`));
 console.log('PASS chart',name);
}
