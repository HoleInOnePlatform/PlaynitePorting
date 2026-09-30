import test from 'node:test';
import assert from 'node:assert/strict';
import { isRestSiteSignal, forwardRestSiteSignal } from './public/signal.js';

const encode = text => new TextEncoder().encode(text);
test('accepts only the intended JSON object', () => {
  assert.equal(isRestSiteSignal(encode('{"type":"rest_site_reached"}')), true);
  for (const value of ['{"type":"handoff.ready"}', '{"type":"rest_site_reached","extra":1}', '[{"type":"rest_site_reached"}]', 'not json', '']) {
    assert.equal(isRestSiteSignal(encode(value)), false);
  }
  assert.equal(isRestSiteSignal(new Uint8Array([0xff])), false);
  assert.equal(isRestSiteSignal(new Uint8Array(257)), false);
});
test('posts the fixed message once the signal is valid', () => {
  const posted = [];
  assert.equal(forwardRestSiteSignal(encode('{"type":"rest_site_reached"}'), { PostMessage: x => posted.push(x) }), true);
  assert.deepEqual(posted, ['{"type":"rest_site_reached"}']);
});
