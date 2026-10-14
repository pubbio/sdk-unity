import assert from 'node:assert/strict';
import fs from 'node:fs';
import vm from 'node:vm';
import test from 'node:test';

function bridge() {
  const sockets = [];
  const memory = new Map();
  const heap = new Int32Array(16);
  let pointer = 100;
  class WebSocket {
    readyState = 1; bufferedAmount = 0; sent = []; closed = false;
    constructor(url) { this.url = url; sockets.push(this); }
    send(text) { this.sent.push(text); }
    close() { this.closed = true; }
  }
  const context = { WebSocket, HEAP32: heap, LibraryManager: { library: {} },
    mergeInto: (target, value) => Object.assign(target, value), UTF8ToString: value => value,
    lengthBytesUTF8: value => Buffer.byteLength(value), _malloc: () => pointer++, _free: value => memory.delete(value),
    stringToUTF8: (value, ptr) => memory.set(ptr, value) };
  vm.createContext(context);
  vm.runInContext(fs.readFileSync(new URL('../Runtime/Plugins/WebGL/Pubb.jslib', import.meta.url), 'utf8'), context);
  context.PubbSockets = context.LibraryManager.library.$PubbSockets;
  return { api: context.LibraryManager.library, sockets, heap, memory, context };
}
test('WebGL transport preserves Unicode and frees polled memory', () => {
  const b = bridge(), id = b.api.Pubb_Connect('wss://example.com/socket');
  b.sockets[0].onmessage({ data: 'merhaba ♟️' });
  const p = b.api.Pubb_Poll(id, 4), json = b.memory.get(p);
  assert.equal(JSON.parse(json).text, 'merhaba ♟️'); assert.equal(b.heap[1], Buffer.byteLength(json));
  b.api.Pubb_Free(p); assert.equal(b.memory.size, 0); assert.equal(b.api.Pubb_Poll(id, 4), 0);
});
test('WebGL send rejects a closed socket or backpressure', () => {
  const b = bridge(), id = b.api.Pubb_Connect('wss://example.com/socket');
  assert.equal(b.api.Pubb_Send(id, 'subscribe'), 0); assert.deepEqual(b.sockets[0].sent, ['subscribe']);
  b.sockets[0].bufferedAmount = 65537; assert.equal(b.api.Pubb_Send(id, 'subscribe'), -1);
  b.api.Pubb_Close(id); assert.equal(b.api.Pubb_Send(id, 'subscribe'), -1); assert.equal(b.sockets[0].closed, true);
});
test('WebGL disposal detaches callbacks and ignores old messages', () => {
  const b = bridge(), id = b.api.Pubb_Connect('wss://example.com/socket'), late = b.sockets[0].onmessage;
  b.api.Pubb_Close(id); late({ data: 'late' }); assert.equal(b.api.Pubb_Poll(id, 4), 0);
  assert.equal(b.sockets[0].onmessage, null); assert.equal(Object.keys(b.context.PubbSockets.sockets).length, 0);
});
test('WebGL queue is bounded and reports overflow', () => {
  const b = bridge(), id = b.api.Pubb_Connect('wss://example.com/socket');
  for (let i = 0; i < 257; i++) b.sockets[0].onmessage({ data: '{}' });
  assert.equal(b.sockets[0].closed, true); assert.equal(JSON.parse(b.memory.get(b.api.Pubb_Poll(id, 4))).kind, 2);
});
