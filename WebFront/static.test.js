import test from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync, existsSync } from 'node:fs';
test('static stream uses scoped backend signaling, preserving media and input', () => {
  const source = readFileSync(new URL('./public/app.js', import.meta.url), 'utf8');
  assert.match(source, /\/v2\/stream\/connect/);
  assert.match(source, /location.hash/);
  assert.match(source, /history.replaceState/);
  assert.match(source, /videoElement/);
  assert.match(source, /audioElement/);
  assert.match(source, /attachInput/);
  assert.doesNotMatch(source, /applicationMessage|CefSharp|postMessage|mock=|\/api\/streams|X-GameLink-Token/);
});
test('preserves vendored WebSDK and license, with no PC server dependencies', () => {
  const pkg = JSON.parse(readFileSync(new URL('./package.json', import.meta.url), 'utf8'));
  assert.equal(pkg.dependencies, undefined);
  assert.equal(pkg.scripts.start, undefined);
  assert.ok(existsSync(new URL('./public/vendor/gameliftstreams.mjs', import.meta.url)));
  assert.ok(existsSync(new URL('./public/vendor/LICENSE.txt', import.meta.url)));
});
