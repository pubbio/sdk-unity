import assert from 'node:assert/strict';
import test from 'node:test';
import { createDemoServer } from '../Samples~/ChatAndLobby/backend/server.mjs';

const token = 'alice-test-token-with-32-characters';
const bobToken = 'bob-test-token-with-32-characters';
test('sample backend authorizes membership, saves chat once, derives identity and rejects score forgery', async t => {
  const publications = [];
  const server = createDemoServer({ players: [{ id: 'alice', name: 'Alice', token }, { id: 'bob', name: 'Bob', token: bobToken }], appId: 'app', secret: 'server-secret', origin: 'https://game.example',
    fetchImpl: async (url, options) => { publications.push({ url, body: JSON.parse(options.body), headers: options.headers }); return Response.json(url.endsWith('/auth') ? { auth: 'key:sig', channel_data: '{"user_id":"alice"}' } : { accepted: true }, { status: url.endsWith('/auth') ? 200 : 202 }); } });
  await new Promise(resolve => server.listen(0, '127.0.0.1', resolve));
  t.after(() => new Promise(resolve => { server.close(resolve); server.closeAllConnections(); }));
  const base = `http://127.0.0.1:${server.address().port}`;
  const request = (path, body, bearer = token) => fetch(base + path, { method: body ? 'POST' : 'GET', headers: { Authorization: `Bearer ${bearer}`, 'Content-Type': 'application/json' }, ...(body ? { body: JSON.stringify(body) } : {}) });
  assert.equal((await request('/rooms/demo/state', null, 'invalid')).status, 401);
  assert.equal((await request('/auth', { socket_id: 'one', channel_name: 'presence-other' })).status, 403);
  assert.equal((await request('/auth', { socket_id: 'one', channel_name: 'presence-lobby-demo', user_id: 'forged' })).status, 200);
  assert.equal(publications[0].body.user_id, 'alice');
  const command = { id: 'cmd1', kind: 'chat', value: 'Merhaba ♟️', playerId: 'forged' };
  assert.equal((await request('/rooms/demo/commands', command)).status, 200);
  assert.equal((await (await request('/rooms/demo/commands', command)).json()).duplicate, true);
  assert.equal((await request('/rooms/demo/commands', { ...command, value: 'Changed' })).status, 409);
  assert.equal((await request('/rooms/demo/commands', { id: 'cmd2', kind: 'score', value: 999 })).status, 400);
  assert.equal((await request('/rooms/demo/commands', { id: 'cmd3', kind: 'ready', value: true })).status, 200);
  const state = await (await request('/rooms/demo/state', null, bobToken)).json();
  assert.equal(state.messages.length, 1); assert.equal(state.messages[0].playerId, 'alice'); assert.equal(state.messages[0].text, 'Merhaba ♟️');
  assert.equal(state.players[0].ready, true); assert.equal(state.players[0].score, 0); assert.equal(state.version, 2);
  assert.equal(JSON.stringify(state).includes(token), false);
  assert.equal((await fetch(base + '/rooms/demo/state', { headers: { Origin: 'https://evil.example' } })).status, 403);
  const preflight = await fetch(base + '/auth', { method: 'OPTIONS', headers: { Origin: 'https://game.example' } });
  assert.equal(preflight.status, 204); assert.equal(preflight.headers.get('access-control-allow-origin'), 'https://game.example');
});
