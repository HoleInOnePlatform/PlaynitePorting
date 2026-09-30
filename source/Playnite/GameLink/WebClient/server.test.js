import test from 'node:test';
import assert from 'node:assert/strict';
import { spawn } from 'node:child_process';
import net from 'node:net';

test('loopback page and token protected APIs', async () => {
  const socket = net.createServer();
  await new Promise(resolve => socket.listen(0, '127.0.0.1', resolve));
  const port = socket.address().port;
  await new Promise(resolve => socket.close(resolve));
  const child = spawn(process.execPath, ['server.js'], {
    cwd: import.meta.dirname,
    env: { ...process.env, PORT: String(port), GAMELINK_TOKEN: 'test-token', STREAM_OPTIONS: 'STS2|a-test|sg-test' },
    stdio: 'ignore'
  });
  try {
    const base = `http://127.0.0.1:${port}`;
    let page;
    for (let i = 0; i < 40; i++) {
      try { page = await fetch(base); break; }
      catch { await new Promise(resolve => setTimeout(resolve, 50)); }
    }
    assert.equal(page?.status, 200);
    assert.match(await page.text(), /GameLink/);
    assert.equal((await fetch(`${base}/api/options`)).status, 403);
    const options = await fetch(`${base}/api/options`, { headers: { 'X-GameLink-Token': 'test-token' } });
    assert.deepEqual(await options.json(), [{ label: 'STS2', index: 0 }]);
    assert.equal((await fetch(`${base}/?mock=1`)).status, 403);
  } finally {
    child.kill();
  }
});
