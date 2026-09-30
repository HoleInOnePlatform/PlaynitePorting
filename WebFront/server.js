import http from 'node:http';
import { readFile } from 'node:fs/promises';
import { resolve, extname } from 'node:path';
import { GameLiftStreamsClient, StartStreamSessionCommand } from '@aws-sdk/client-gameliftstreams';

const root = resolve('public');
const port = Number(process.env.PORT || 8000);
const aws = new GameLiftStreamsClient({ region: process.env.AWS_REGION });
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
  if (req.method === 'GET' && url.pathname === '/api/options') {
    return send(res, 200, resources.map(({ label }, index) => ({ label, index })));
  }
  if (req.method === 'POST' && url.pathname === '/api/streams') {
    if (req.headers.origin && req.headers.origin !== `http://${req.headers.host}` && req.headers.origin !== `https://${req.headers.host}`) return send(res, 403, { error: 'Origin rejected' });
    let body = '';
    try {
      for await (const chunk of req) {
        body += chunk;
        if (body.length > 100_000) throw new Error('Request too large');
      }
      const { index, signalRequest } = JSON.parse(body);
      const option = resources[index];
      if (!Number.isInteger(index) || !option || typeof signalRequest !== 'string' || !signalRequest || signalRequest.length > 90_000) return send(res, 400, { error: 'Invalid stream request' });
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
}).listen(port, () => console.log(`GameLink web client on http://localhost:${port}`));
