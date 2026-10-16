'use strict';

const assert = require('node:assert/strict');
const fs = require('node:fs');
const os = require('node:os');
const path = require('node:path');
const test = require('node:test');

const { readIdentity, encodeIdentityHeader } = require('../../claude/account-identity');

function configFile(content) {
  const file = path.join(fs.mkdtempSync(path.join(os.tmpdir(), 'claude-identity-')), '.claude.json');
  if (content !== undefined) fs.writeFileSync(file, content);
  return file;
}

// Field names pinned against the 2.1.285 binary strings; the matching key carries both uuids
// because the same person in two organisations is two accounts.
test('identity is read from oauthAccount in ~/.claude.json', () => {
  const file = configFile(JSON.stringify({
    hasCompletedOnboarding: true,
    oauthAccount: {
      accountUuid: 'acc-uuid', emailAddress: ' me@example.com ', organizationUuid: 'org-uuid',
      organizationName: 'Example\u0007 Org', organizationRole: 'admin'
    }
  }));

  assert.deepEqual(readIdentity(file), {
    key: 'acc-uuid:org-uuid', email: 'me@example.com', organization: 'Example Org'
  });
});

test('a config without oauthAccount, a missing file, or junk yields no identity', () => {
  assert.equal(readIdentity(configFile(JSON.stringify({ projects: {} }))), null);
  assert.equal(readIdentity(configFile(JSON.stringify({ oauthAccount: [] }))), null);
  assert.equal(readIdentity(configFile(JSON.stringify({ oauthAccount: { emailAddress: 7 } }))), null);
  assert.equal(readIdentity(configFile('not json')), null);
  assert.equal(readIdentity(configFile()), null);
});

test('a partial account still yields what it has', () => {
  const identity = readIdentity(configFile(JSON.stringify({ oauthAccount: { emailAddress: 'only@example.com' } })));
  assert.deepEqual(identity, { key: null, email: 'only@example.com', organization: null });
});

test('the header is base64url JSON so non-ASCII organisation names survive', () => {
  const header = encodeIdentityHeader({ key: 'k', email: 'e@example.com', organization: 'Örg' });
  assert.match(header, /^[A-Za-z0-9_-]+$/);
  assert.deepEqual(JSON.parse(Buffer.from(header, 'base64url').toString('utf8')),
    { key: 'k', email: 'e@example.com', organization: 'Örg' });
  assert.equal(encodeIdentityHeader(null), null);
});
