import test from 'node:test';
import assert from 'node:assert/strict';
import { parseIdList, withCredentialSelection } from '../credentials.mjs';

test('parseIdList reads comma-separated ids and the two words for "all" and "none"', () => {
  assert.deepEqual(parseIdList('a, b,,a'), ['a', 'b']);
  assert.equal(parseIdList(undefined), undefined);
  assert.equal(parseIdList(''), undefined);
  assert.equal(parseIdList(' * '), undefined);
  assert.deepEqual(parseIdList('none'), []);
  assert.deepEqual(parseIdList('NONE'), []);
});

test('withCredentialSelection turns the text parameters into what the REST API takes', () => {
  assert.deepEqual(
    withCredentialSelection({ title: 'x', credentialId: ' acct ', gitPatIds: 'p1,p2' }),
    { title: 'x', credentialId: 'acct', gitPatIds: ['p1', 'p2'] });
  // Unspecified means the field is absent, so the backend applies its own defaults.
  assert.deepEqual(withCredentialSelection({ title: 'x', credentialId: '', gitPatIds: '' }), { title: 'x' });
  assert.deepEqual(withCredentialSelection({ title: 'x', gitPatIds: 'none' }), { title: 'x', gitPatIds: [] });
  assert.deepEqual(withCredentialSelection(undefined), {});
});
