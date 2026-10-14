import http from 'node:http';
import { pathToFileURL } from 'node:url';

// A single-room teaching sample. Sessions are supplied by the operator, never minted from client user IDs.
export function createDemoServer({ players, appId, secret, apiUrl = 'https://api.pubb.io', origin, fetchImpl = fetch }) {
  if (!players.length || players.some(p => !p.id || !p.name || !p.token || p.token.length < 24) || new Set(players.map(p => p.token)).size !== players.length || new Set(players.map(p => p.id)).size !== players.length)
    throw new Error('Configure distinct players with id, name and a random token of at least 24 characters.');
  const sessions = new Map(players.map(p => [p.token, p]));
  const state = { version: 0, players: players.map(({ id, name }) => ({ id, name, ready: false, score: 0 })), messages: [] };
  const commands = new Map();
  const rates = new Map();
  const outbox = [];
  let flushing = false;
  const channel = 'presence-lobby-demo';
  const endpoint = new URL(apiUrl);
  if (endpoint.protocol !== 'https:' && !['localhost', '127.0.0.1', '[::1]'].includes(endpoint.hostname)) throw new Error('Use HTTPS for pubb.');

  async function pubb(path, body, key) {
    const response = await fetchImpl(`${apiUrl}/api/apps/${encodeURIComponent(appId)}/${path}`, {
      method: 'POST', redirect: 'error', signal: AbortSignal.timeout(10000),
      headers: { Authorization: `Bearer ${secret}`, 'Content-Type': 'application/json', ...(key ? { 'Idempotency-Key': key } : {}) },
      body: JSON.stringify(body),
    });
    if (response.status !== (path === 'auth' ? 200 : 202)) throw new Error('pubb request failed');
    return response.json();
  }
  async function flush() {
    if (flushing) return;
    flushing = true;
    try {
      while (outbox.length) {
        const event = outbox[0];
        await pubb('events', { channel, name: event.name, data: event.data }, event.key);
        outbox.shift();
      }
    } catch { /* Keep the same idempotency key and retry; HTTP reads remain authoritative. */ }
    finally { flushing = false; }
  }
  const timer = setInterval(() => void flush(), 2000);
  timer.unref();
  const server = http.createServer(async (req, res) => {
    const reply = (status, body) => {
      res.writeHead(status, { 'Content-Type': 'application/json', 'Cache-Control': 'no-store' });
      res.end(JSON.stringify(body));
    };
    if (req.headers.origin) {
      if (!origin || req.headers.origin !== origin) return reply(403, { error: 'Origin denied' });
      res.setHeader('Access-Control-Allow-Origin', origin);
      res.setHeader('Vary', 'Origin');
      res.setHeader('Access-Control-Allow-Headers', 'Authorization, Content-Type');
      res.setHeader('Access-Control-Allow-Methods', 'GET, POST, OPTIONS');
    }
    if (req.method === 'OPTIONS') { res.writeHead(204); res.end(); return; }
    const player = sessions.get((req.headers.authorization || '').replace(/^Bearer /, ''));
    if (!player) return reply(401, { error: 'Sign in first' });
    const window = Math.floor(Date.now() / 1000);
    let rate = rates.get(player.id);
    if (!rate || rate.window !== window) { rate = { window, count: 0 }; rates.set(player.id, rate); }
    if (++rate.count > 10) { res.setHeader('Retry-After', '1'); return reply(429, { error: 'Slow down' }); }
    if (req.method === 'GET' && req.url === '/rooms/demo/state') return reply(200, state);
    if (req.method !== 'POST' || !['/auth', '/rooms/demo/commands'].includes(req.url)) return reply(404, { error: 'Not found' });
    try {
      const chunks = []; let size = 0;
      for await (const chunk of req) {
        size += chunk.length; chunks.push(chunk);
        if (size > 2048) { reply(413, { error: 'Body too large' }); return; }
      }
      let input;
      try { input = JSON.parse(Buffer.concat(chunks).toString('utf8')); } catch { return reply(400, { error: 'Invalid JSON' }); }
      if (!input || typeof input !== 'object' || Array.isArray(input)) return reply(400, { error: 'Expected an object' });
      if (req.url === '/auth') {
        if (input.channel_name !== channel) return reply(403, { error: 'Channel denied' });
        if (typeof input.socket_id !== 'string' || !/^[a-zA-Z0-9.-]{1,128}$/.test(input.socket_id)) return reply(400, { error: 'Invalid socket ID' });
        const authorization = await pubb('auth', { socket_id: input.socket_id, channel_name: channel, user_id: player.id, user_info: { name: player.name } });
        return reply(200, authorization);
      }
      if (typeof input.id !== 'string' || !/^[a-zA-Z0-9-]{1,64}$/.test(input.id)) return reply(400, { error: 'Invalid command ID' });
      if (!((input.kind === 'chat' && typeof input.value === 'string' && input.value.trim().length > 0 && input.value.length <= 500) || (input.kind === 'ready' && typeof input.value === 'boolean')))
        return reply(400, { error: 'Expected a chat message or ready boolean' });
      const key = `${player.id}:${input.id}`;
      const fingerprint = JSON.stringify([input.kind, input.value]);
      if (commands.has(key)) return reply(commands.get(key) === fingerprint ? 200 : 409, { duplicate: true });
      // Bound memory without silently forgetting idempotency records in a running demo session.
      if (commands.size >= 10000 || outbox.length >= 1000) return reply(503, { error: 'Demo capacity reached' });
      state.version++;
      let name, data;
      if (input.kind === 'chat') {
        data = { id: key, playerId: player.id, name: player.name, text: input.value.trim(), version: state.version };
        state.messages.push(data); if (state.messages.length > 50) state.messages.shift();
        name = 'chat.message';
      } else {
        state.players.find(p => p.id === player.id).ready = input.value;
        name = 'lobby.updated'; data = { version: state.version };
      }
      commands.set(key, fingerprint);
      outbox.push({ name, data, key: `unity-${state.version}-${input.id}` });
      reply(200, { saved: true, version: state.version });
      void flush();
    } catch { if (!res.headersSent) reply(502, { error: 'Backend request failed' }); }
  });
  server.on('close', () => clearInterval(timer));
  return server;
}

if (process.argv[1] && import.meta.url === pathToFileURL(process.argv[1]).href) {
  const { PUBB_APP_ID, PUBB_APP_SECRET, DEMO_PLAYERS_JSON, WEBGL_ORIGIN } = process.env;
  if (!PUBB_APP_ID || !PUBB_APP_SECRET || !DEMO_PLAYERS_JSON) throw new Error('Set PUBB_APP_ID, PUBB_APP_SECRET and DEMO_PLAYERS_JSON. See README.');
  const server = createDemoServer({ appId: PUBB_APP_ID, secret: PUBB_APP_SECRET, players: JSON.parse(DEMO_PLAYERS_JSON), origin: WEBGL_ORIGIN });
  server.listen(3210, '127.0.0.1', () => console.log('Unity sample backend: http://localhost:3210'));
}
