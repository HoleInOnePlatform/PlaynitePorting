import http from 'node:http';
import { readFile } from 'node:fs/promises';
import { resolve, extname } from 'node:path';

const root = resolve('public');
const port = Number(process.env.PORT || 8000);
const token = process.env.GAMELINK_TOKEN;
if (!token) throw new Error('GAMELINK_TOKEN is required');
const resources = (process.env.STREAM_OPTIONS || '').split(',').map(entry => {
  const [label, applicationIdentifier, identifier] = entry.split('|');
  return { label, applicationIdentifier, identifier };
}).filter(x => x.label && x.applicationIdentifier && x.identifier);
const types = { '.html': 'text/html; charset=utf-8', '.css': 'text/css; charset=utf-8', '.js': 'text/javascript; charset=utf-8', '.mjs': 'text/javascript; charset=utf-8' };

function send(res, status, data, type = 'application/json; charset=utf-8') {
  res.writeHead(status, { 'Content-Type': type, 'Cache-Control': 'no-store', 'X-Content-Type-Options': 'nosniff' });
  res.end(type.startsWith('application/json') ? JSON.stringify(data) : data);
}

http.createServer(async (req, res) => {
  const url = new URL(req.url, 'http://localhost');
  if (req.headers.host !== `127.0.0.1:${port}` || req.headers['sec-fetch-site'] === 'cross-site') return send(res, 403, { error: 'Host rejected' });
  if (url.searchParams.get('mock') === '1' && process.env.GAMELINK_MOCK !== '1') return send(res, 403, { error: 'Mock disabled' });
  if (url.pathname.startsWith('/api/') && req.headers['x-gamelink-token'] !== token) return send(res, 403, { error: 'Token rejected' });
  if (req.method === 'GET' && url.pathname === '/api/options') {
    return send(res, 200, resources.map(({ label }, index) => ({ label, index })));
  }
  if (req.method === 'POST' && url.pathname === '/api/streams') {
    if (req.headers.origin !== `http://127.0.0.1:${port}`) return send(res, 403, { error: 'Origin rejected' });
    let body = '';
    try {
      for await (const chunk of req) {
        body += chunk;
        if (body.length > 100_000) throw new Error('Request too large');
      }
      const { index, signalRequest } = JSON.parse(body);
      const option = resources[index];
      if (!Number.isInteger(index) || !option || typeof signalRequest !== 'string' || !signalRequest || signalRequest.length > 90_000) return send(res, 400, { error: 'Invalid stream request' });
      const { GameLiftStreamsClient, StartStreamSessionCommand } = await import('@aws-sdk/client-gameliftstreams');
      const aws = new GameLiftStreamsClient({ region: process.env.AWS_REGION });
      const session = await aws.send(new StartStreamSessionCommand({
        Identifier: option.identifier,
        ApplicationIdentifier: option.applicationIdentifier,
        Protocol: 'WebRTC',
        SignalRequest: signalRequest,
        ConnectionTimeoutSeconds: 300,
        SessionLengthSeconds: 7200
      }));
      return send(res, 201, { signalResponse: session.SignalResponse, sessionId: session.Id });
    } catch (error) {
      console.error('Stream start failed:', error.name || 'Error');
      return send(res, 502, { error: 'Stream could not be started' });
    }
  }
  if (req.method !== 'GET') return send(res, 405, { error: 'Method not allowed' });
  const name = url.pathname === '/' ? '/index.html' : url.pathname;
  if (!/^\/(?:index\.html|app\.js|signal\.js|style\.css|vendor\/gameliftstreams\.mjs)$/.test(name)) return send(res, 404, { error: 'Not found' });
  try { return send(res, 200, await readFile(resolve(root, `.${name}`)), types[extname(name)]); }
  catch { return send(res, 404, { error: 'Not found' }); }
}).listen(port, '127.0.0.1', () => console.log(`GameLink web client on http://127.0.0.1:${port}`));
