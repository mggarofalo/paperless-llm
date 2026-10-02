import { test } from 'node:test';
import assert from 'node:assert/strict';
import { shouldPromote, stableVersion } from './release-policy.mjs';

test('first and newer stable versions can promote', () => {
  assert.equal(shouldPromote('v0.2.0', []), true);
  assert.equal(shouldPromote('v0.2.0', ['v0.1.3']), true);
  assert.equal(shouldPromote('v0.10.0', ['v0.9.9']), true);
  assert.equal(shouldPromote('v1.0.0', ['v0.99.99']), true);
});
test('older release never moves channels backward regardless of input order', () => {
  assert.equal(shouldPromote('v0.2.0', ['v0.1.3', 'v0.3.0', 'v0.2.0']), false);
  assert.equal(shouldPromote('v1.9.99', ['v1.10.0']), false);
});
test('same immutable version may repair partially promoted channels', () => {
  assert.equal(shouldPromote('v0.2.0', ['v0.2.0', 'v0.1.3']), true);
});
test('prereleases and malformed tags cannot be promotion candidates', () => {
  for (const tag of ['v0.2.0-rc.1', 'v0.2.0+build', '0.2.0', 'v01.2.0', 'v1.2', 'latest']) {
    assert.equal(stableVersion(tag), null);
    assert.throws(() => shouldPromote(tag, []));
  }
});
test('unrelated and prerelease history does not displace a stable channel', () => {
  assert.equal(shouldPromote('v0.2.0', ['v1.0.0-rc.1', 'notes', 'v0.1.3']), true);
});
